using System.Runtime.CompilerServices;

namespace Wlrix.Files.Core.Search;

/// <summary>How a search ended.</summary>
/// <param name="Found">How many entries matched.</param>
/// <param name="Searched">How many directories were read.</param>
/// <param name="Unreadable">How many could not be, usually for want of permission.</param>
/// <param name="Truncated">Whether it stopped at the query's limit rather than at the end.</param>
public readonly record struct SearchOutcome(int Found, int Searched, int Unreadable, bool Truncated);

/// <summary>
/// Walks a tree looking for names, streaming what it finds.
/// </summary>
/// <remarks>
/// Streamed rather than collected, for the same reason a directory listing is: the first hit
/// should be on screen while the rest of the tree is still being read, and a search of a home
/// directory that produced nothing for ten seconds and then everything would be indisting-
/// uishable from a hang.
///
/// <para>
/// Breadth first, which is not the obvious choice and is the right one. A depth-first walk
/// disappears into the first subdirectory it meets and can spend a minute deep inside
/// <c>.cache</c> before it looks at anything beside it; breadth first returns the shallow
/// matches first, and the shallow matches are overwhelmingly the ones somebody was looking for.
/// </para>
///
/// <para>
/// Entries come out exactly as the filesystem reported them, names included. Rewriting a name
/// to show where the file was found is tempting and wrong: the name is what a rename dialog
/// fills in and what the type is resolved from, and <c>Location.Child</c> refuses a name with
/// a separator in it — so a result called <c>src/deep/notes.txt</c> could not be renamed at
/// all. Showing the path is the view's business, and <c>FileEntryViewModel.DisplayName</c> is
/// where it happens.
/// </para>
///
/// <para>
/// Nothing here follows a symlink. A link pointing at its own ancestor makes the walk endless,
/// and one pointing outside the tree makes it answer with files the search was not asked about.
/// </para>
/// </remarks>
public static class FileSearch
{
    /// <summary>Searches <paramref name="root"/> and everything under it.</summary>
    /// <remarks>
    /// An unreadable directory is counted and skipped rather than ending the search: a home
    /// directory usually has at least one, and stopping there would throw away every result
    /// after it.
    /// </remarks>
    public static async IAsyncEnumerable<FileEntry> RunAsync(
        IFileSystemProvider provider,
        Location root,
        SearchQuery query,
        Action<SearchOutcome>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!query.IsUsable)
            yield break;

        var pending = new Queue<Location>();
        pending.Enqueue(root);

        var found = 0;
        var searched = 0;
        var unreadable = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();

            IAsyncEnumerator<FileEntry>? entries = null;
            try
            {
                var fs = await provider.GetAsync(directory, cancellationToken).ConfigureAwait(false);
                entries = fs.EnumerateAsync(directory, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (FileOperationException)
            {
                unreadable++;
                progress?.Invoke(new SearchOutcome(found, searched, unreadable, false));
                continue;
            }

            searched++;

            try
            {
                while (true)
                {
                    FileEntry entry;
                    try
                    {
                        if (!await entries.MoveNextAsync().ConfigureAwait(false))
                            break;
                        entry = entries.Current;
                    }
                    catch (FileOperationException)
                    {
                        // Partway through a listing. What was already yielded stays yielded,
                        // and the rest of the tree is still worth reading.
                        unreadable++;
                        break;
                    }

                    if (entry.IsHidden && !query.IncludeHidden)
                        continue;

                    if (entry.IsDirectory && entry.SymlinkTarget is null)
                        pending.Enqueue(entry.Location);

                    if (!query.Matches(entry.Name))
                        continue;

                    found++;
                    yield return entry;

                    if (found >= query.MaxResults)
                    {
                        progress?.Invoke(new SearchOutcome(found, searched, unreadable, true));
                        yield break;
                    }
                }
            }
            finally
            {
                await entries.DisposeAsync().ConfigureAwait(false);
            }

            progress?.Invoke(new SearchOutcome(found, searched, unreadable, false));
        }

        progress?.Invoke(new SearchOutcome(found, searched, unreadable, false));
    }
}
