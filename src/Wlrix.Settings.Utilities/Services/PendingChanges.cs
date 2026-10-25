// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Platform;

namespace Wlrix.Settings.Utilities.Services;

/// <summary>
/// Everything chosen since the panel opened or last applied, waiting for Apply.
/// </summary>
/// <remarks>
/// Both pages edit the same thing. A browser chosen on the first page is a new order for the
/// web types, and the second page shows that order when one of them is selected, so the two
/// can never disagree about what Apply is going to write.
/// </remarks>
internal sealed class PendingChanges
{
    /// <summary>The application order for a type, best first, replacing the user's.</summary>
    public Dictionary<string, IReadOnlyList<string>> Orders { get; } = new(StringComparer.Ordinal);

    /// <summary>A type's new user package: a changed system type, or a new one.</summary>
    public Dictionary<string, UserMimeType> TypeEdits { get; } = new(StringComparer.Ordinal);

    /// <summary>User packages to delete: a reverted system type, or a removed user one.</summary>
    public HashSet<string> TypeDeletions { get; } = new(StringComparer.Ordinal);

    /// <summary>The terminal's entry id, if it was changed.</summary>
    public string? Terminal { get; set; }

    /// <summary>Raised after any change, so Apply and Reset can follow.</summary>
    public event Action? Changed;

    public bool IsEmpty =>
        Orders.Count == 0 && TypeEdits.Count == 0 && TypeDeletions.Count == 0 && Terminal is null;

    /// <summary>Whether any change touches the type database, which Apply then recompiles.</summary>
    public bool TouchesTypes => TypeEdits.Count > 0 || TypeDeletions.Count > 0;

    public void SetOrder(string mimeType, IReadOnlyList<string> order)
    {
        Orders[mimeType] = order;
        Changed?.Invoke();
    }

    public void RemoveOrders(IEnumerable<string> mimeTypes)
    {
        foreach (var type in mimeTypes)
            Orders.Remove(type);
        Changed?.Invoke();
    }

    public void EditType(UserMimeType type)
    {
        TypeDeletions.Remove(type.Type);
        TypeEdits[type.Type] = type;
        Changed?.Invoke();
    }

    public void DeleteType(string mimeType)
    {
        TypeEdits.Remove(mimeType);
        TypeDeletions.Add(mimeType);
        Changed?.Invoke();
    }

    public void SetTerminal(string? id)
    {
        Terminal = id;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Orders.Clear();
        TypeEdits.Clear();
        TypeDeletions.Clear();
        Terminal = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Writes one type's preference order into the user's <c>mimeapps.list</c>.
    /// </summary>
    /// <remarks>
    /// All three groups, because one alone cannot say it. The whole order goes into
    /// <c>[Default Applications]</c>, which is what keeps the second and third choices in
    /// their places for when the first is uninstalled. An application the user added that
    /// does not claim the type goes into <c>[Added Associations]</c>, or other readers would
    /// not offer it at all. An application that claims the type and was taken out of the
    /// order goes into <c>[Removed Associations]</c>, or it comes back on the next read.
    /// </remarks>
    /// <param name="list">The user's file, edited in place.</param>
    /// <param name="mimeType">The type.</param>
    /// <param name="order">The order wanted, best first.</param>
    /// <param name="baseline">
    /// What claims the type without the user's file: the entries' own <c>MimeType=</c> plus
    /// the system files' additions. See <see cref="ApplicationHandlers.Associated"/>.
    /// </param>
    public static void Write(MimeAppsList list, string mimeType, IReadOnlyList<string> order, IReadOnlyList<string> baseline)
    {
        list.SetEntries(MimeAppsList.DefaultApplications, mimeType, order);
        list.SetEntries(MimeAppsList.AddedAssociations, mimeType,
            [.. order.Where(id => !baseline.Contains(id, StringComparer.Ordinal))]);
        list.SetEntries(MimeAppsList.RemovedAssociations, mimeType,
            [.. baseline.Where(id => !order.Contains(id, StringComparer.Ordinal))]);
    }
}
