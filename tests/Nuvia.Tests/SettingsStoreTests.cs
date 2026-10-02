using System;
using System.IO;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for <see cref="SettingsStore"/> — focused on the Saved Messages "show external files" toggle
/// added for the rebuild feature. These write a real JSON file in a temp folder; no network, no
/// credentials, and the store never persists anything sensitive.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _path;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "nuvia-settings-" + Guid.NewGuid().ToString("N") + ".json");
        _store = new SettingsStore(_path);
    }

    [Fact]
    public void ShowExternal_DefaultsToFalse_WhenNothingStored()
    {
        Assert.False(_store.GetShowExternalSavedMessages(7));
    }

    [Fact]
    public void ShowExternal_SetTrue_IsReadBack_AndPersistsAcrossInstances()
    {
        _store.SetShowExternalSavedMessages(7, true);
        Assert.True(_store.GetShowExternalSavedMessages(7));

        // A fresh store over the same file must read the persisted value (survives an app restart).
        var reopened = new SettingsStore(_path);
        Assert.True(reopened.GetShowExternalSavedMessages(7));
    }

    [Fact]
    public void ShowExternal_SetBackToFalse_IsReadBack()
    {
        _store.SetShowExternalSavedMessages(7, true);
        _store.SetShowExternalSavedMessages(7, false);
        Assert.False(_store.GetShowExternalSavedMessages(7));

        var reopened = new SettingsStore(_path);
        Assert.False(reopened.GetShowExternalSavedMessages(7));
    }

    [Fact]
    public void ShowExternal_IsPerAccount()
    {
        _store.SetShowExternalSavedMessages(7, true);

        // A different account's preference is independent and stays at the default.
        Assert.True(_store.GetShowExternalSavedMessages(7));
        Assert.False(_store.GetShowExternalSavedMessages(8));
    }

    [Fact]
    public void ShowExternal_Account0_ReturnsFalse_AndSetThrows()
    {
        // Account id 0 is "no real account" (demo mode): it has no stored preference and cannot set one.
        Assert.False(_store.GetShowExternalSavedMessages(0));
        Assert.Throws<ArgumentException>(() => _store.SetShowExternalSavedMessages(0, true));
    }

    [Fact]
    public void ShowExternal_AndDefaultDestination_DoNotClobberEachOther()
    {
        _store.SetPreferredDestinationKind(7, StorageDestinationKind.ManagedGroup);
        _store.SetShowExternalSavedMessages(7, true);

        var reopened = new SettingsStore(_path);
        Assert.Equal(StorageDestinationKind.ManagedGroup, reopened.GetPreferredDestinationKind(7));
        Assert.True(reopened.GetShowExternalSavedMessages(7));

        // Flipping one back does not disturb the other.
        reopened.SetShowExternalSavedMessages(7, false);
        Assert.Equal(StorageDestinationKind.ManagedGroup, reopened.GetPreferredDestinationKind(7));
        Assert.False(reopened.GetShowExternalSavedMessages(7));
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best-effort temp cleanup */ }
    }
}
