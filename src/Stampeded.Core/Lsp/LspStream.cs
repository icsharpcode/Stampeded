using System.Text;

namespace Stampeded.Core.Lsp;

/// <summary>
/// JSON-RPC's framing as LSP uses it: a <c>Content-Length</c> header, a blank line, and that
/// many bytes of UTF-8. Both ends of a connection need it - the client talking to a server,
/// and a server of ours talking back - so it lives apart from either.
/// </summary>
public static class LspStream
{
	/// <summary>Reads one message, or null at the end of the stream.</summary>
	public static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct)
	{
		int length = await ReadHeaderAsync(stream, ct);
		if (length < 0)
			return null;
		var payload = new byte[length];
		int read = 0;
		while (read < length)
		{
			int got = await stream.ReadAsync(payload.AsMemory(read), ct);
			if (got <= 0)
				return null;
			read += got;
		}
		return payload;
	}

	public static async Task WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
	{
		var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
		await stream.WriteAsync(header, ct);
		await stream.WriteAsync(payload, ct);
		await stream.FlushAsync(ct);
	}

	/// <summary>The Content-Length of the message that follows, or -1 at the end of the
	/// stream. Header bytes are read one at a time: buffering would eat the body.</summary>
	static async Task<int> ReadHeaderAsync(Stream stream, CancellationToken ct)
	{
		const string ContentLength = "Content-Length:";
		var line = new StringBuilder();
		var single = new byte[1];
		int length = -1;
		while (true)
		{
			int got = await stream.ReadAsync(single.AsMemory(), ct);
			if (got <= 0)
				return -1;
			if (single[0] != (byte)'\n')
			{
				if (single[0] != (byte)'\r')
					line.Append((char)single[0]);
				continue;
			}
			if (line.Length == 0)
				return length; // the blank line: the body starts here
			string header = line.ToString();
			if (header.StartsWith(ContentLength, StringComparison.OrdinalIgnoreCase)
				&& int.TryParse(header[ContentLength.Length..].Trim(), out int parsed))
			{
				length = parsed;
			}
			line.Clear();
		}
	}
}
