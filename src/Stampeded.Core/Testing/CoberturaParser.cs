using System.Xml.Linq;

namespace Stampeded.Core.Testing;

/// <summary>
/// Parses cobertura coverage XML into per-file line hit counts, keyed by root-relative
/// path with '/' separators. Class entries sharing a file merge by max hits.
/// </summary>
public static class CoberturaParser
{
	public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> Parse(string xml, string rootPath)
	{
		var doc = XDocument.Parse(xml);
		var sources = doc.Descendants("source").Select(s => s.Value.Trim()).ToList();
		string root = Path.GetFullPath(rootPath);
		var result = new Dictionary<string, Dictionary<int, int>>();

		foreach (var cls in doc.Descendants("class"))
		{
			string? fileName = (string?)cls.Attribute("filename");
			if (string.IsNullOrEmpty(fileName))
				continue;
			string? relative = Resolve(fileName, sources, root);
			if (relative is null)
				continue;
			if (!result.TryGetValue(relative, out var lines))
				result[relative] = lines = [];
			foreach (var line in cls.Descendants("line"))
			{
				if ((int?)line.Attribute("number") is not { } number || (int?)line.Attribute("hits") is not { } hits)
					continue;
				lines[number] = Math.Max(lines.GetValueOrDefault(number), hits);
			}
		}
		return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<int, int>)kv.Value);
	}

	static string? Resolve(string fileName, List<string> sources, string root)
	{
		var candidates = Path.IsPathRooted(fileName)
			? [fileName]
			: sources.Count > 0 ? sources.Select(s => Path.Combine(s, fileName)) : [Path.Combine(root, fileName)];
		foreach (var candidate in candidates)
		{
			string full = Path.GetFullPath(candidate);
			if (full.StartsWith(root, StringComparison.Ordinal) && full.Length > root.Length)
				return full[(root.Length + 1)..].Replace('\\', '/');
		}
		return null;
	}
}
