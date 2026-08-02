using CliWrap.Buffered;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Roslyn;

public enum SemanticState
{
	NotLoaded,
	Restoring,
	Loading,
	Ready,
	SyntaxOnly,
	Failed,
}

public sealed record SymbolLocation(string FilePath, TextSpan Span, int Line);

public sealed record ReferenceHit(string FilePath, int Line, TextSpan Span, string LineText);

/// <summary>
/// Source semantics over one checked-out worktree: an MSBuildWorkspace when the solution
/// loads (NuGet restore is run first so the design-time build sees its references), an
/// AdhocWorkspace over all .cs files otherwise. One instance per review session; dispose
/// and reload on PR switch, never patch incrementally.
/// </summary>
public sealed class RoslynWorkspaceService : IDisposable
{
	Workspace? workspace;
	Solution? solution;
	Dictionary<string, DocumentId>? documentsByPath;
	string worktreePath = "";

	public SemanticState State { get; private set; } = SemanticState.NotLoaded;
	public string StateDetail { get; private set; } = "";
	public event Action? StateChanged;

	void SetState(SemanticState state, string detail = "")
	{
		State = state;
		StateDetail = detail;
		StateChanged?.Invoke();
	}

	public async Task LoadAsync(string worktree, CancellationToken ct)
	{
		worktreePath = worktree;
		try
		{
			string? sln = Directory.EnumerateFiles(worktree, "*.sln", SearchOption.TopDirectoryOnly)
				.OrderByDescending(f => new FileInfo(f).Length)
				.FirstOrDefault();
			if (sln is not null)
			{
				SetState(SemanticState.Restoring, Path.GetFileName(sln));
				await RestoreAsync(sln, ct);
				SetState(SemanticState.Loading, Path.GetFileName(sln));
				var msbuild = MSBuildWorkspace.Create();
				var loaded = await msbuild.OpenSolutionAsync(sln, cancellationToken: ct);
				if (loaded.Projects.Any(p => p.Documents.Any()))
				{
					workspace = msbuild;
					solution = loaded;
					IndexDocuments();
					SetState(SemanticState.Ready, Path.GetFileName(sln));
					return;
				}
				msbuild.Dispose();
			}
			LoadSyntaxOnly(worktree, ct);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// A broken solution load must not take navigation down with it: degrade to
			// the syntax-only workspace, which cannot fail structurally.
			try
			{
				LoadSyntaxOnly(worktree, ct);
				SetState(SemanticState.SyntaxOnly, ex.Message);
			}
			catch (Exception inner)
			{
				SetState(SemanticState.Failed, inner.Message);
			}
		}
	}

	async Task RestoreAsync(string sln, CancellationToken ct)
	{
		// Pruning would rewrite committed packages.lock.json files in repos that carry
		// full lock files (e.g. ILSpy); locked-mode restores also depend on them staying
		// whole. SHA1 signature acceptance matches the local OpenSSL setup.
		var result = await CliWrap.Cli.Wrap("dotnet")
			.WithArguments(["restore", sln, "-p:RestoreEnablePackagePruning=false"])
			.WithWorkingDirectory(worktreePath)
			.WithEnvironmentVariables(env => env.Set("OPENSSL_ENABLE_SHA1_SIGNATURES", "1"))
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		if (result.ExitCode != 0)
			throw new ToolFailedException("dotnet restore", result.ExitCode, result.StandardError);
	}

	void LoadSyntaxOnly(string worktree, CancellationToken ct)
	{
		SetState(SemanticState.Loading, "syntax-only");
		var adhoc = new AdhocWorkspace();
		var project = adhoc.AddProject("Worktree", LanguageNames.CSharp)
			.AddMetadataReferences(TrustedPlatformAssemblies());
		foreach (var file in Directory.EnumerateFiles(worktree, "*.cs", SearchOption.AllDirectories))
		{
			ct.ThrowIfCancellationRequested();
			if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
				|| file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
				continue;
			project = project.AddDocument(Path.GetFileName(file), SourceText.From(File.ReadAllText(file)), filePath: file).Project;
		}
		workspace = adhoc;
		solution = project.Solution;
		IndexDocuments();
		SetState(SemanticState.SyntaxOnly);
	}

	static IEnumerable<MetadataReference> TrustedPlatformAssemblies()
	{
		if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa)
			return [];
		return tpa.Split(Path.PathSeparator)
			.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
	}

	void IndexDocuments()
	{
		documentsByPath = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
		foreach (var project in solution!.Projects)
		{
			foreach (var document in project.Documents)
			{
				// First project wins for linked/multi-targeted files; any compilation's
				// view is good enough for navigation.
				if (document.FilePath is not null)
					documentsByPath.TryAdd(document.FilePath, document.Id);
			}
		}
	}

	Document? GetDocument(string absolutePath)
	{
		if (solution is null || documentsByPath is null)
			return null;
		return documentsByPath.TryGetValue(absolutePath, out var id) ? solution.GetDocument(id) : null;
	}

	public string ToAbsolutePath(string repoRelativePath)
		=> Path.Combine(worktreePath, repoRelativePath);

	public string? ToRelativePath(string absolutePath)
	{
		string full = Path.GetFullPath(absolutePath);
		string root = Path.GetFullPath(worktreePath);
		return full.StartsWith(root, StringComparison.Ordinal)
			? full[(root.Length + 1)..].Replace('\\', '/')
			: null;
	}

	/// <summary>Spans of identifier-like classified tokens, for clickable reference segments.</summary>
	public async Task<IReadOnlyList<TextSpan>> GetIdentifierSpansAsync(string repoRelativePath, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return [];
		var text = await document.GetTextAsync(ct);
		var classified = await Classifier.GetClassifiedSpansAsync(document, new TextSpan(0, text.Length), ct);
		return classified
			.Where(c => IsIdentifierClassification(c.ClassificationType))
			.Select(c => c.TextSpan)
			.Distinct()
			.OrderBy(s => s.Start)
			.ToList();
	}

	static bool IsIdentifierClassification(string type) => type is
		ClassificationTypeNames.ClassName or ClassificationTypeNames.StructName or
		ClassificationTypeNames.InterfaceName or ClassificationTypeNames.EnumName or
		ClassificationTypeNames.DelegateName or ClassificationTypeNames.RecordClassName or
		ClassificationTypeNames.RecordStructName or ClassificationTypeNames.TypeParameterName or
		ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName or
		ClassificationTypeNames.PropertyName or ClassificationTypeNames.FieldName or
		ClassificationTypeNames.EventName or ClassificationTypeNames.ConstantName or
		ClassificationTypeNames.EnumMemberName or ClassificationTypeNames.LocalName or
		ClassificationTypeNames.ParameterName or ClassificationTypeNames.NamespaceName;

	/// <summary>Absolute text position for a 1-based (line, column) in a worktree file.</summary>
	public async Task<int?> GetPositionAsync(string repoRelativePath, int line, int column, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return null;
		var text = await document.GetTextAsync(ct);
		if (line < 1 || line > text.Lines.Count)
			return null;
		var textLine = text.Lines[line - 1];
		return textLine.Start + Math.Clamp(column - 1, 0, textLine.Span.Length);
	}

	public async Task<ISymbol?> GetSymbolAtAsync(string repoRelativePath, int position, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return null;
		var semanticModel = await document.GetSemanticModelAsync(ct);
		if (semanticModel is null)
			return null;
		var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, workspace!, ct);
		return symbol;
	}

	public SymbolLocation? GetDefinitionLocation(ISymbol symbol)
	{
		var location = symbol.OriginalDefinition.Locations.FirstOrDefault(l => l.IsInSource);
		if (location is null || location.SourceTree?.FilePath is not { Length: > 0 } path)
			return null;
		var line = location.GetLineSpan().StartLinePosition.Line + 1;
		return new SymbolLocation(path, location.SourceSpan, line);
	}

	public async Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(ISymbol symbol, CancellationToken ct)
	{
		if (solution is null)
			return [];
		var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
		var hits = new List<ReferenceHit>();
		foreach (var reference in references)
		{
			foreach (var location in reference.Locations)
			{
				var tree = location.Location.SourceTree;
				if (tree?.FilePath is not { Length: > 0 } path)
					continue;
				var text = await tree.GetTextAsync(ct);
				var lineSpan = location.Location.GetLineSpan();
				int line = lineSpan.StartLinePosition.Line + 1;
				string lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
				hits.Add(new ReferenceHit(path, line, location.Location.SourceSpan, lineText));
			}
		}
		return hits
			.DistinctBy(h => (h.FilePath, h.Span.Start))
			.OrderBy(h => h.FilePath, StringComparer.Ordinal)
			.ThenBy(h => h.Line)
			.ToList();
	}

	public async Task<string?> GetHoverTextAsync(string repoRelativePath, int position, CancellationToken ct)
	{
		var symbol = await GetSymbolAtAsync(repoRelativePath, position, ct);
		if (symbol is null)
			return null;
		string signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
		string xml = symbol.GetDocumentationCommentXml(cancellationToken: ct) ?? "";
		string summary = ExtractSummary(xml);
		return summary.Length > 0 ? $"{signature}\n{summary}" : signature;
	}

	static string ExtractSummary(string xml)
	{
		int start = xml.IndexOf("<summary>", StringComparison.Ordinal);
		int end = xml.IndexOf("</summary>", StringComparison.Ordinal);
		if (start < 0 || end < 0)
			return "";
		string inner = xml[(start + "<summary>".Length)..end];
		// Collapse whitespace and strip simple tags; a rich renderer can come later.
		inner = System.Text.RegularExpressions.Regex.Replace(inner, "<[^>]+>", "");
		return System.Text.RegularExpressions.Regex.Replace(inner, @"\s+", " ").Trim();
	}

	public void Dispose()
	{
		workspace?.Dispose();
		workspace = null;
		solution = null;
		SetState(SemanticState.NotLoaded);
	}
}
