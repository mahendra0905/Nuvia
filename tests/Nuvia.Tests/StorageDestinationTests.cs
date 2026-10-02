using System;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for the storage-destination model (<see cref="StorageDestination"/>,
/// <see cref="ManagedStorageGroup"/>). These pin the two-and-only-two destinations rule and the
/// guard that a group with missing identifiers can never become a usable destination. No network,
/// no credentials.
/// </summary>
public sealed class StorageDestinationTests
{
    private static ManagedStorageGroup UsableGroup() =>
        new(AccountId: 42, ChannelId: 100, AccessHash: 999, Title: "Vault", CreatedUtc: DateTime.UtcNow);

    [Fact]
    public void SavedMessages_IsTheDefaultKind()
    {
        Assert.Equal(StorageDestinationKind.SavedMessages, StorageDestination.SavedMessages.Kind);
        Assert.Null(StorageDestination.SavedMessages.Group);
        Assert.Equal("Saved Messages", StorageDestination.SavedMessages.DisplayName);
    }

    [Fact]
    public void ForGroup_WithUsableGroup_ProducesManagedDestination()
    {
        var group = UsableGroup();
        var dest = StorageDestination.ForGroup(group);

        Assert.Equal(StorageDestinationKind.ManagedGroup, dest.Kind);
        Assert.Same(group, dest.Group);
        Assert.Equal("Vault", dest.DisplayName);
    }

    [Fact]
    public void ForGroup_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => StorageDestination.ForGroup(null!));
    }

    [Theory]
    [InlineData(0, 100, 999)] // no account
    [InlineData(42, 0, 999)]  // no channel
    [InlineData(42, 100, 0)]  // no access hash
    public void ForGroup_WithUnusableGroup_Throws(long accountId, long channelId, long accessHash)
    {
        var group = new ManagedStorageGroup(accountId, channelId, accessHash, "X", DateTime.UtcNow);
        Assert.False(group.LooksUsable());
        Assert.Throws<ArgumentException>(() => StorageDestination.ForGroup(group));
    }

    [Fact]
    public void LooksUsable_TrueOnlyWhenAllIdentifiersPresent()
    {
        Assert.True(UsableGroup().LooksUsable());
    }

    [Fact]
    public void ManagedGroup_WithBlankTitle_FallsBackToGenericLabel()
    {
        var group = new ManagedStorageGroup(42, 100, 999, "   ", DateTime.UtcNow);
        var dest = StorageDestination.ForGroup(group);
        Assert.Equal("Private group", dest.DisplayName);
    }

    [Fact]
    public void DisplayName_NeverEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(StorageDestination.SavedMessages.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(StorageDestination.ForGroup(UsableGroup()).DisplayName));
    }
}
