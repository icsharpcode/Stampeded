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

public sealed record SemanticToken(int Line, int Column, int Length, string Classification);

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
		CliLog.Write("semantics", $"{state} {detail}".TrimEnd());
		State = state;
		StateDetail = detail;
		StateChanged?.Invoke();
	}

	/// <summary>Accumulated restore/load output and diagnostics, for the load-log view.</summary>
	public string LoadLog { get; private set; } = "";

	void Log(string message)
	{
		LoadLog += message + "\n";
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
				try
				{
					await RestoreAsync(sln, cleanRetry: false, ct);
				}
				catch (ToolFailedException ex)
				{
					// Common causes: stale packages.lock.json on the PR branch (the repo
					// may restore in locked mode) or broken obj/ state from an earlier
					// interrupted restore in this cached worktree. The worktree is a
					// disposable copy, so retry clean and unlocked.
					Log("First restore failed, retrying clean with --force-evaluate:");
					Log(ex.Message);
					SetState(SemanticState.Restoring, "clean retry");
					await RestoreAsync(sln, cleanRetry: true, ct);
				}
				SetState(SemanticState.Loading, Path.GetFileName(sln));
				var msbuild = MSBuildWorkspace.Create();
				var loaded = await msbuild.OpenSolutionAsync(sln, cancellationToken: ct);
				foreach (var diagnostic in msbuild.Diagnostics)
					Log($"[workspace] {diagnostic.Kind}: {diagnostic.Message}");
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
			Log(ex.ToString());
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

	async Task RestoreAsync(string sln, bool cleanRetry, CancellationToken ct)
	{
		if (cleanRetry)
			DeleteBuildArtifacts();
		// Pruning would rewrite committed packages.lock.json files in repos that carry
		// full lock files (e.g. ILSpy); locked-mode restores also depend on them staying
		// whole - except on the clean retry, where the lock files themselves may be the
		// problem and this worktree copy is free to regenerate them.
		string[] args = cleanRetry
			? ["restore", sln, "-p:RestoreEnablePackagePruning=false", "--force", "--force-evaluate", "-p:RestoreLockedMode=false"]
			: ["restore", sln, "-p:RestoreEnablePackagePruning=false"];
		var result = await CliWrap.Cli.Wrap("dotnet")
			.WithArguments(args)
			.WithWorkingDirectory(worktreePath)
			.WithEnvironmentVariables(env => env.Set("OPENSSL_ENABLE_SHA1_SIGNATURES", "1"))
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		if (result.ExitCode != 0)
		{
			// NuGet reports most errors on stdout, not stderr; keep both.
			throw new ToolFailedException("dotnet restore", result.ExitCode,
				result.StandardError + "\n" + Tail(result.StandardOutput, 4000));
		}
		Log($"restore ok ({(cleanRetry ? "clean retry" : "normal")})");
		CliLog.Write("dotnet", $"restore {(cleanRetry ? "(clean retry) " : "")}-> exit 0");
	}

	static string Tail(string text, int maxChars)
		=> text.Length <= maxChars ? text : text[^maxChars..];

	void DeleteBuildArtifacts()
	{
		foreach (var dir in Directory.EnumerateDirectories(worktreePath, "*", SearchOption.AllDirectories)
			.Where(d => Path.GetFileName(d) is "obj" or "bin")
			.OrderByDescending(d => d.Length))
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (IOException)
			{
				// Locked or already gone: the restore that follows will cope or fail loudly.
			}
		}
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

	/// <summary>
	/// Identifier-like classified tokens as (1-based line, column, length, classification).
	/// Feeds both semantic colouring and the clickable reference segments; identifiers never
	/// span lines, so line/column addressing is exact and maps through the diff line map.
	/// </summary>
	public async Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(string repoRelativePath, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return [];
		var text = await document.GetTextAsync(ct);
		var classified = await Classifier.GetClassifiedSpansAsync(document, new TextSpan(0, text.Length), ct);
		var tokens = new List<SemanticToken>();
		foreach (var span in classified)
		{
			if (!IsIdentifierClassification(span.ClassificationType))
				continue;
			var lineSpan = text.Lines.GetLinePositionSpan(span.TextSpan);
			if (lineSpan.Start.Line != lineSpan.End.Line)
				continue;
			tokens.Add(new SemanticToken(
				lineSpan.Start.Line + 1,
				lineSpan.Start.Character + 1,
				span.TextSpan.Length,
				span.ClassificationType));
		}
		return tokens
			.DistinctBy(t => (t.Line, t.Column, t.Length))
			.OrderBy(t => t.Line).ThenBy(t => t.Column)
			.ToList();
	}

	/// <summary>IDE-style quick info (signature, docs, ...) as plain text sections.</summary>
	public async Task<string?> GetQuickInfoAsync(string repoRelativePath, int position, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return null;
		var service = Microsoft.CodeAnalysis.QuickInfo.QuickInfoService.GetService(document);
		if (service is null)
			return null;
		var info = await service.GetQuickInfoAsync(document, position, ct);
		if (info is null || info.Sections.IsEmpty)
			return null;
		var sections = info.Sections
			.Select(s => s.Text)
			.Where(t => !string.IsNullOrWhiteSpace(t));
		string result = string.Join("\n\n", sections);
		return result.Length > 0 ? result : null;
	}

	/// <summary>All reference and definition occurrences of a symbol within one file, for
	/// in-document occurrence highlighting.</summary>
	public async Task<IReadOnlyList<SemanticToken>> FindOccurrencesInFileAsync(
		ISymbol symbol, string repoRelativePath, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null || solution is null)
			return [];
		var text = await document.GetTextAsync(ct);
		var occurrences = new List<SemanticToken>();

		void Add(TextSpan span, string kind)
		{
			var lineSpan = text.Lines.GetLinePositionSpan(span);
			if (lineSpan.Start.Line != lineSpan.End.Line)
				return;
			occurrences.Add(new SemanticToken(
				lineSpan.Start.Line + 1, lineSpan.Start.Character + 1, span.Length, kind));
		}

		var references = await SymbolFinder.FindReferencesAsync(
			symbol, solution, [document], ct);
		foreach (var reference in references)
		{
			foreach (var location in reference.Locations)
			{
				if (location.Document.Id == document.Id)
					Add(location.Location.SourceSpan, "reference");
			}
			foreach (var location in reference.Definition.Locations)
			{
				if (location.IsInSource && location.SourceTree == await document.GetSyntaxTreeAsync(ct))
					Add(location.SourceSpan, "definition");
			}
		}
		return occurrences
			.DistinctBy(t => (t.Line, t.Column))
			.OrderBy(t => t.Line).ThenBy(t => t.Column)
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
