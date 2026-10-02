using System.Linq;
using System.Windows;
using Nuvia.App.Services;

namespace Nuvia.App.QuickUpload;

/// <summary>The concrete decision a quick-upload should act on, after any chooser/confirm dialog.</summary>
public abstract record DestinationPlan;

/// <summary>Send the file to the account's Saved Messages.</summary>
public sealed record UseSavedMessages : DestinationPlan;

/// <summary>Send the file to an existing managed private group.</summary>
public sealed record UseExistingGroup(ManagedStorageGroup Group) : DestinationPlan;

/// <summary>Create a new private group with this (user-confirmed) title, then upload to it.</summary>
public sealed record CreateNewGroup(string Title) : DestinationPlan;

/// <summary>The user backed out — do nothing.</summary>
public sealed record CancelQuickUpload : DestinationPlan;

/// <summary>
/// Turns a coarse <see cref="QuickUploadDestination"/> hint into a concrete <see cref="DestinationPlan"/>,
/// showing the small chooser and/or the create-group confirmation where the hint leaves a choice open.
/// <para>
/// This is UI-decision only (modal dialogs on the UI thread); it performs no Telegram RPC. The single
/// short create-group RPC is left to the caller so it can drive its own busy indicator. A quick upload runs
/// from the shell without the app's open-location context, so its "Group" choice targets the account's
/// first usable managed group (creating a fresh one when none exists yet); the full multi-group chooser
/// belongs to the main window.
/// </para>
/// </summary>
public static class QuickUploadDestinationPlanner
{
    public static DestinationPlan PlanDestination(
        Window? owner,
        string filePath,
        QuickUploadDestination hint,
        long accountId,
        IndexStore index,
        ITelegramStorage storage)
    {
        var groups = accountId > 0 ? index.GetStorageGroups(accountId) : System.Array.Empty<ManagedStorageGroup>();
        var group = groups.FirstOrDefault(g => g.LooksUsable());
        var groupUsable = group is not null;

        // Multiple groups are allowed now, so always offer to create when storage is available — even when a
        // group already exists. The shell verb has no open location to target, so a create makes a new group.
        var canCreate = storage.IsAvailable;

        switch (hint)
        {
            case QuickUploadDestination.Saved:
                return new UseSavedMessages();

            case QuickUploadDestination.Group:
                if (groupUsable)
                    return new UseExistingGroup(group!);
                if (canCreate)
                    return PromptCreate(owner);
                // Chosen group cannot resolve and cannot be (re)created — fall back rather than fail.
                return new UseSavedMessages();

            default: // Ask
                var choice = QuickUploadChooserWindow.Ask(
                    owner,
                    filePath: filePath,
                    existingGroupTitle: groupUsable ? group!.Title : null,
                    canCreate: canCreate);

                return choice switch
                {
                    QuickUploadChoice.SavedMessages => new UseSavedMessages(),
                    QuickUploadChoice.ExistingGroup when groupUsable => new UseExistingGroup(group!),
                    QuickUploadChoice.CreateGroup when canCreate => PromptCreate(owner),
                    _ => new CancelQuickUpload(),
                };
        }
    }

    private static DestinationPlan PromptCreate(Window? owner)
    {
        // Explicit confirmation with an editable name. Nothing is created unless this returns true.
        var dialog = new CreateGroupWindow();
        if (owner is not null)
            dialog.Owner = owner;

        if (dialog.ShowDialog() != true)
            return new CancelQuickUpload();

        return new CreateNewGroup(dialog.GroupName);
    }
}
