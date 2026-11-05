namespace RDCore.PlatformInstaller;

/// <summary>A response's content read whole into memory, but never more than a limit of it.</summary>
internal static class BoundedContent
{
    /// <summary>
    /// The content of <paramref name="response"/>, refused with <paramref name="tooLarge"/>, which makes the exception for a size
    /// it has at least, as soon as it is known to be more than <paramref name="limit"/> bytes: from its <c>Content-Length</c>
    /// before anything is read, or as it is read when it has none. Read the response with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, or HttpClient buffers all of it first.
    /// </summary>
    public static async Task<byte[]> ReadAsync(HttpResponseMessage response, long limit, Func<long, Exception> tooLarge,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > limit)
        {
            throw tooLarge(length);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw tooLarge(buffer.Length + read);
            }

            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
