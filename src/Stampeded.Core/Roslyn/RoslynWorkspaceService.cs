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

public sealed record ChangedMember(string Display, string Kind, int FirstLine);

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
	Solution? loadedSolution;
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
					solution = loadedSolution = loaded;
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
			.WithEnvironmentVariables(env => {
				env.Set("OPENSSL_ENABLE_SHA1_SIGNATURES", "1");
				ExternalTool.StripMsBuildLocatorVariables(env);
			})
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		if (result.ExitCode != 0)
		{
			// NuGet reports most errors on stdout, not stderr; keep both, and when both
			// are empty capture the host environment - an output-less exit 1 points at
			// the dotnet host itself rather than at MSBuild/NuGet.
			Log($"restore exit {result.ExitCode}; stdout {result.StandardOutput.Length} chars, stderr {result.StandardError.Length} chars");
			Log(Tail(result.StandardOutput, 4000));
			Log(result.StandardError);
			CliLog.Write("dotnet", $"restore failed (exit {result.ExitCode}); stdout={result.StandardOutput.Length}ch stderr={result.StandardError.Length}ch");
			if (result.StandardOutput.Length == 0 && result.StandardError.Length == 0)
				await LogHostDiagnosticsAsync(ct);
			throw new ToolFailedException("dotnet restore", result.ExitCode,
				result.StandardError + "\n" + Tail(result.StandardOutput, 4000));
		}
		Log($"restore ok ({(cleanRetry ? "clean retry" : "normal")})");
		CliLog.Write("dotnet", $"restore {(cleanRetry ? "(clean retry) " : "")}-> exit 0");
	}

	static string Tail(string text, int maxChars)
		=> text.Length <= maxChars ? text : text[^maxChars..];

	async Task LogHostDiagnosticsAsync(CancellationToken ct)
	{
		foreach (var name in new[] { "PATH", "DOTNET_ROOT", "DOTNET_HOST_PATH", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildExtensionsPath" })
			CliLog.Write("env", $"{name}={Environment.GetEnvironmentVariable(name) ?? "(unset)"}");
		try
		{
			var info = await CliWrap.Cli.Wrap("dotnet")
				.WithArguments(["--version"])
				.WithWorkingDirectory(worktreePath)
				.WithValidation(CliWrap.CommandResultValidation.None)
				.ExecuteBufferedAsync(ct);
			CliLog.Write("env", $"dotnet --version -> exit {info.ExitCode}: {info.StandardOutput.Trim()} {info.StandardError.Trim()}");
		}
		catch (Exception ex)
		{
			CliLog.Write("env", $"dotnet --version failed to start: {ex.Message}");
		}
	}

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
		solution = loadedSolution = project.Solution;
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

	/// <summary>
	/// Substitutes the text of some files, so that every position-based answer - symbols,
	/// occurrences, quick info, classification - describes the revision being displayed
	/// rather than the one that was loaded. Positions are offsets into a specific text;
	/// reading one commit of a file that later commits change makes those two different
	/// texts, and the whole stack has to agree on which one it is talking about.
	/// </summary>
	public void SetTextOverlay(IReadOnlyDictionary<string, string> textByRelativePath)
	{
		if (loadedSolution is null || documentsByPath is null)
			return;
		var overlaid = loadedSolution;
		foreach (var (relativePath, text) in textByRelativePath)
		{
			if (documentsByPath.TryGetValue(ToAbsolutePath(relativePath), out var id))
				overlaid = overlaid.WithDocumentText(id, SourceText.From(text));
		}
		solution = overlaid;
	}

	/// <summary>Back to the revision this workspace was loaded for.</summary>
	public void ClearTextOverlay() => solution = loadedSolution;

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
		return document is null ? [] : await ClassifyAsync(document, ct);
	}

	/// <summary>
	/// Classifies a revision of a file that this workspace does not hold - one commit of a
	/// file that later commits change. The loaded document is forked with the given text,
	/// so the project's references and its other files still stand behind it and names
	/// still resolve; only this file's content differs. Positions are exact for the text
	/// given, which is what applying them to the view requires.
	/// </summary>
	public async Task<IReadOnlyList<SemanticToken>> GetSemanticTokensForTextAsync(
		string repoRelativePath, string text, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		return document is null ? [] : await ClassifyAsync(document.WithText(SourceText.From(text)), ct);
	}

	static async Task<IReadOnlyList<SemanticToken>> ClassifyAsync(Document document, CancellationToken ct)
	{
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
	/// <summary>
	/// This workspace's copy of a file. Token positions only mean anything against the
	/// exact text they were computed from, so a caller displaying some other revision has
	/// to check before using them.
	/// </summary>
	public async Task<string?> GetDocumentTextAsync(string repoRelativePath, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return null;
		return (await document.GetTextAsync(ct)).ToString();
	}

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

	/// <summary>All member display strings declared in a file (for classifying map
	/// entries as added vs modified vs removed across base/head).</summary>
	public async Task<IReadOnlySet<string>> ListMemberDisplaysAsync(string repoRelativePath, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return new HashSet<string>();
		var semanticModel = await document.GetSemanticModelAsync(ct);
		var root = await document.GetSyntaxRootAsync(ct);
		if (semanticModel is null || root is null)
			return new HashSet<string>();
		var displays = new HashSet<string>();
		foreach (var node in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax>())
		{
			if (node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseFieldDeclarationSyntax fields)
			{
				foreach (var variable in fields.Declaration.Variables)
				{
					if (semanticModel.GetDeclaredSymbol(variable, ct) is { } fieldSymbol)
						displays.Add(fieldSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat));
				}
			}
			else if (semanticModel.GetDeclaredSymbol(node, ct) is { } symbol)
			{
				displays.Add(symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat));
			}
		}
		return displays;
	}

	/// <summary>The distinct members (methods/properties/types/...) containing the given
	/// 1-based lines of a file, for the symbol-level change map.</summary>
	public async Task<IReadOnlyList<ChangedMember>> MapLinesToMembersAsync(
		string repoRelativePath, IReadOnlyCollection<int> lines, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return [];
		var semanticModel = await document.GetSemanticModelAsync(ct);
		var text = await document.GetTextAsync(ct);
		if (semanticModel is null)
			return [];
		var members = new Dictionary<string, ChangedMember>();
		foreach (int line in lines)
		{
			if (line < 1 || line > text.Lines.Count)
				continue;
			var textLine = text.Lines[line - 1];
			string content = textLine.ToString();
			int indent = content.Length - content.TrimStart().Length;
			if (content.Trim().Length == 0)
				continue;
			int position = textLine.Start + indent;
			var symbol = semanticModel.GetEnclosingSymbol(position, ct);
			// Walk up to the member users think in: method/property/field/event/ctor,
			// falling back to the containing type for lines outside any member.
			ISymbol? member = symbol;
			while (member is not null
				and not IMethodSymbol and not IPropertySymbol and not IFieldSymbol
				and not IEventSymbol and not INamedTypeSymbol)
			{
				member = member.ContainingSymbol;
			}
			if (member is IMethodSymbol { AssociatedSymbol: { } associated })
				member = associated; // accessors report as their property/event
			if (member is null || member is INamespaceSymbol)
				continue;
			string display = member.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
			if (members.TryGetValue(display, out var existing))
			{
				if (line < existing.FirstLine)
					members[display] = existing with { FirstLine = line };
			}
			else
			{
				members[display] = new ChangedMember(display, MemberKindOf(member), line);
			}
		}
		return members.Values.OrderBy(m => m.FirstLine).ToList();
	}

	/// <summary>Icon-grade member kind: named types report their TypeKind (Class, Struct,
	/// Interface, Enum, Delegate), methods split off Constructor and Operator; everything
	/// else is its SymbolKind (Method, Property, Field, Event).</summary>
	static string MemberKindOf(ISymbol member) => member switch {
		INamedTypeSymbol type => type.TypeKind.ToString(),
		IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "Constructor",
		IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "Operator",
		IPropertySymbol { IsIndexer: true } => "Indexer",
		_ => member.Kind.ToString(),
	};

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

	/// <summary>
	/// The symbol at a column, falling back to any identifier on the same line. A caret
	/// sits wherever it was left - often in the indentation - and a command aimed at "the
	/// symbol here" should still find the one the line is about.
	/// </summary>
	public async Task<ISymbol?> GetSymbolOnLineAsync(
		string repoRelativePath, int line, int preferredColumn, CancellationToken ct)
	{
		var document = GetDocument(ToAbsolutePath(repoRelativePath));
		if (document is null)
			return null;
		var text = await document.GetTextAsync(ct);
		if (line < 1 || line > text.Lines.Count)
			return null;
		var semanticModel = await document.GetSemanticModelAsync(ct);
		var root = await document.GetSyntaxRootAsync(ct);
		if (semanticModel is null || root is null)
			return null;
		var textLine = text.Lines[line - 1];
		var positions = new List<int>();
		int preferred = textLine.Start + preferredColumn - 1;
		if (preferredColumn >= 1 && preferred < textLine.End)
			positions.Add(preferred);
		foreach (var token in root.DescendantTokens(TextSpan.FromBounds(textLine.Start, textLine.End)))
		{
			if (token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken))
				positions.Add(token.SpanStart);
		}
		foreach (int position in positions)
		{
			if (await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, workspace!, ct) is { } symbol)
				return symbol;
		}
		return null;
	}

	public SymbolLocation? GetDefinitionLocation(ISymbol symbol)
	{
		var location = symbol.OriginalDefinition.Locations.FirstOrDefault(l => l.IsInSource);
		if (location is null || location.SourceTree?.FilePath is not { Length: > 0 } path)
			return null;
		var line = location.GetLineSpan().StartLinePosition.Line + 1;
		return new SymbolLocation(path, location.SourceSpan, line);
	}

	/// <summary>File path of the PE reference defining <paramref name="symbol"/>, for
	/// metadata symbols without source. Only already-realized compilations are consulted;
	/// the compilation that produced the symbol necessarily is one.</summary>
	public string? TryGetMetadataAssemblyPath(ISymbol symbol)
	{
		if (solution is null || symbol.ContainingAssembly is not { } assembly)
			return null;
		foreach (var project in solution.Projects)
		{
			if (project.TryGetCompilation(out var compilation)
				&& compilation.GetMetadataReference(assembly) is PortableExecutableReference { FilePath: { } path })
			{
				return path;
			}
		}
		return null;
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

	/// <summary>
	/// One level of the call hierarchy around a symbol. Callers come from the whole
	/// solution; callees from the member's own body. Each node carries the declaration
	/// position of the member it names, which is what lets the tree expand another level.
	/// </summary>
	public async Task<IReadOnlyList<CallNode>> GetCallsAsync(ISymbol symbol, CallDirection direction, CancellationToken ct)
		=> direction == CallDirection.Callers
			? await GetCallersAsync(symbol, ct)
			: await GetCalleesAsync(symbol, ct);

	async Task<IReadOnlyList<CallNode>> GetCallersAsync(ISymbol symbol, CancellationToken ct)
	{
		if (solution is null)
			return [];
		var callers = await SymbolFinder.FindCallersAsync(symbol, solution, ct);
		var nodes = new List<CallNode>();
		foreach (var caller in callers)
		{
			// Indirect callers reach the symbol through an interface or override; they are
			// real consequences of a change, so they are kept and not marked apart.
			var sites = new List<CallSite>();
			foreach (var location in caller.Locations.Where(l => l.IsInSource))
				sites.Add(await ToSiteAsync(location, ct));
			nodes.Add(ToNode(caller.CallingSymbol, sites));
		}
		return Order(nodes);
	}

	async Task<IReadOnlyList<CallNode>> GetCalleesAsync(ISymbol symbol, CancellationToken ct)
	{
		var sitesByTarget = new Dictionary<ISymbol, List<CallSite>>(SymbolEqualityComparer.Default);
		foreach (var reference in symbol.DeclaringSyntaxReferences)
		{
			var syntax = await reference.GetSyntaxAsync(ct);
			var document = solution?.GetDocument(syntax.SyntaxTree);
			if (document is null)
				continue;
			var semanticModel = await document.GetSemanticModelAsync(ct);
			if (semanticModel is null)
				continue;
			foreach (var node in syntax.DescendantNodes())
			{
				// Invocations and constructions are the calls a reader cares about;
				// property and field accesses would bury them.
				if (node is not (Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax
					or Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax))
				{
					continue;
				}
				if (semanticModel.GetSymbolInfo(node, ct).Symbol is not { } target)
					continue;
				if (!sitesByTarget.TryGetValue(target.OriginalDefinition, out var sites))
					sitesByTarget[target.OriginalDefinition] = sites = [];
				sites.Add(await ToSiteAsync(node.GetLocation(), ct));
			}
		}
		return Order([.. sitesByTarget.Select(pair => ToNode(pair.Key, pair.Value))]);
	}

	/// <summary>A call location with the source line it sits on, for display.</summary>
	static async Task<CallSite> ToSiteAsync(Location location, CancellationToken ct)
	{
		var lineSpan = location.GetLineSpan();
		int line = lineSpan.StartLinePosition.Line + 1;
		string preview = "";
		if (location.SourceTree is { } tree)
		{
			var text = await tree.GetTextAsync(ct);
			if (line <= text.Lines.Count)
				preview = text.Lines[line - 1].ToString().Trim();
		}
		return new CallSite(location.SourceTree?.FilePath ?? "", line, preview);
	}

	static IReadOnlyList<CallNode> Order(IReadOnlyList<CallNode> nodes)
		=> nodes
			.DistinctBy(n => (n.ContainingType, n.Display, n.FilePath, n.Line))
			.OrderBy(n => n.ContainingType, StringComparer.Ordinal)
			.ThenBy(n => n.Display, StringComparer.Ordinal)
			.ToList();

	static CallNode ToNode(ISymbol symbol, IReadOnlyList<CallSite> sites)
	{
		var location = symbol.OriginalDefinition.Locations.FirstOrDefault(l => l.IsInSource);
		var lineSpan = location?.GetLineSpan();
		return new CallNode(
			symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
			symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "",
			location?.SourceTree?.FilePath,
			(lineSpan?.StartLinePosition.Line ?? 0) + 1,
			(lineSpan?.StartLinePosition.Character ?? 0) + 1,
			sites);
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
		solution = loadedSolution = null;
		SetState(SemanticState.NotLoaded);
	}
}
