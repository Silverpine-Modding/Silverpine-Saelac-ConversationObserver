using BepInEx;
using HarmonyLib;
using Silverpine.ModdingTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ConversationObserver;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    "1.9.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "salt.silverpine.conversationobserver";
    public const string PluginName = "Conversation Observer";
    public const string PluginVersion = "1.0.1";

    private Harmony _harmony;

    private void Awake()
    {
        ConversationObserverController.RegisterAction();
        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll();
    }

}

internal static class ConversationObserverController
{
    private const string StepAwayActionId =
        Plugin.PluginGuid + ".step-away";
    private const string ActingButtonPrefix = "Speak as ";

    private static readonly FieldInfo InputField =
        AccessTools.Field(typeof(DialogBox), "inputField");
    private static readonly FieldInfo TalkButtonField =
        AccessTools.Field(typeof(DialogBox), "talkButton");
    private static readonly FieldInfo GlobalCanvasField =
        AccessTools.Field(typeof(DialogBox), "globalCanvasGroup");
    private static readonly FieldInfo NpcDialogSpriteField =
        AccessTools.Field(typeof(NeuralNPC), "dialogSprite");
    private static NeuralNPC _lastActingNpc;

    internal static bool IsPlayerAway { get; private set; }

    internal static void RegisterAction()
    {
        DialogueActions.Register(
            Plugin.PluginGuid,
            new DialogueActionDefinition
            {
                Id = StepAwayActionId,
                Label = "Step Away",
                Order = -200,
                RequireNpc = true,
                IsVisible = _ =>
                    !IsPlayerAway && GetParticipants().Count >= 2,
                OnSelected = _ => StepAway()
            });
    }

    private static void StepAway()
    {
        List<NeuralNPC> participants = GetParticipants();
        if (IsPlayerAway)
        {
            return;
        }
        if (participants.Count < 2)
        {
            Notify("At least two NPCs must be in the conversation.");
            return;
        }

        NeuralNPC nextSpeaker = ChooseNextNpc(
            NeuralNPC.currentActiveDialogNeuralNPC);
        if (nextSpeaker == null)
        {
            Notify("No other NPC is available to continue the conversation.");
            return;
        }

        IsPlayerAway = true;
        string playerName = GetPlayerName();
        string participantNames = NeuralNPC.ToAnd(
            participants.Select(npc => npc.GetFinalName()).ToList());
        AddSystemTurn(
            participants,
            playerName + " stepped away and is no longer present in this "
            + "conversation. The only present speakers are "
            + participantNames + ". Do not address, consult, wait for, or "
            + "select " + playerName + " as a speaker until a system message "
            + "says that " + playerName + " returned.");

        Notify(
            playerName + " stepped away. Use Interrupt to rejoin the "
            + "conversation.");
        NeuralNPC.OnMultiInputCallback(nextSpeaker, "");
    }

    internal static NeuralNPC ChooseNextNpc(NeuralNPC speaker)
    {
        List<NeuralNPC> participants = GetParticipants();
        if (participants.Count < 2)
        {
            return null;
        }

        int speakerIndex = participants.IndexOf(speaker);
        if (speakerIndex < 0)
        {
            speakerIndex = participants.IndexOf(
                NeuralNPC.currentActiveDialogNeuralNPC);
        }

        for (int offset = 1; offset <= participants.Count; offset++)
        {
            int index = speakerIndex < 0
                ? offset - 1
                : (speakerIndex + offset) % participants.Count;
            NeuralNPC candidate = participants[index];
            if (candidate != speaker || participants.Count == 1)
            {
                return candidate;
            }
        }

        return participants[0];
    }

    internal static bool TryGetCounterpart(out NeuralNPC counterpart)
    {
        counterpart = null;
        if (!IsPlayerAway)
        {
            return false;
        }

        NeuralNPC speaker = NeuralNPC.currentActiveDialogNeuralNPC;
        NeuralNPC actingNpc = DialogueInputActors.CurrentNpc;
        if (actingNpc != null && actingNpc != speaker &&
            HasLoadedPortrait(actingNpc))
        {
            counterpart = actingNpc;
            return true;
        }

        List<NeuralNPC> candidates = GetParticipants()
            .Where(npc => npc != speaker && HasLoadedPortrait(npc))
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        counterpart = candidates[0];
        return true;
    }

    private static bool HasLoadedPortrait(NeuralNPC npc)
    {
        return npc != null && NpcDialogSpriteField.GetValue(npc) is Sprite;
    }

    internal static void UpdateActingInput()
    {
        if (!IsPlayerAway || DialogBox.Instance == null ||
            !DialogBox.Instance.isOpen)
        {
            return;
        }

        var input = (TMP_InputField)InputField.GetValue(DialogBox.Instance);
        var talkButton = (Button)TalkButtonField.GetValue(DialogBox.Instance);
        if (input == null || talkButton == null)
        {
            return;
        }

        TextMeshProUGUI label =
            talkButton.GetComponentInChildren<TextMeshProUGUI>();
        if (GetParticipants().Count < 2)
        {
            bool wasWaitingForNpc =
                talkButton.gameObject.activeSelf && label != null &&
                (string.Equals(
                     label.text,
                     "Continue",
                     StringComparison.OrdinalIgnoreCase) ||
                 label.text.StartsWith(
                     ActingButtonPrefix,
                     StringComparison.OrdinalIgnoreCase));
            Rejoin();
            if (wasWaitingForNpc)
            {
                DialogBox.Instance.StopContinueOnlyMode();
            }
            return;
        }

        if (!talkButton.gameObject.activeSelf)
        {
            return;
        }

        if (label == null ||
            (!string.Equals(
                 label.text,
                 "Continue",
                 StringComparison.OrdinalIgnoreCase) &&
             !label.text.StartsWith(
                 ActingButtonPrefix,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        NeuralNPC actingNpc = DialogueInputActors.CurrentNpc;
        if (!ReferenceEquals(actingNpc, _lastActingNpc) &&
            !DialogBox.Instance.playerSwitchAnimationPlaying)
        {
            NeuralNPC active = NeuralNPC.currentActiveDialogNeuralNPC;
            active?.DoStartNPCMode(DialogBox.SpriteSwitchMode.Normal);
            _lastActingNpc = actingNpc;
        }
        if (actingNpc == null)
        {
            label.text = "Continue";
            input.gameObject.SetActive(false);
            return;
        }

        label.text = ActingButtonPrefix + actingNpc.GetFinalName();
        if (!input.gameObject.activeSelf)
        {
            input.gameObject.SetActive(true);
            input.ActivateInputField();
        }
    }

    internal static bool TrySubmitActingInput()
    {
        if (!IsPlayerAway || DialogueInputActors.CurrentNpc == null ||
            DialogBox.Instance == null)
        {
            return false;
        }

        var input = (TMP_InputField)InputField.GetValue(DialogBox.Instance);
        var globalCanvas =
            (CanvasGroup)GlobalCanvasField.GetValue(DialogBox.Instance);
        if (input == null || string.IsNullOrWhiteSpace(input.text))
        {
            return true;
        }
        if (globalCanvas == null || !globalCanvas.blocksRaycasts)
        {
            return false;
        }

        string text = input.text;
        if (!DialogueInputActors.TrySubmit(text))
        {
            Notify("The selected NPC input add-on could not submit that line.");
            return true;
        }

        input.text = "";
        return true;
    }

    internal static void Rejoin()
    {
        if (!IsPlayerAway)
        {
            return;
        }

        List<NeuralNPC> participants = GetParticipants();
        IsPlayerAway = false;
        _lastActingNpc = null;
        string playerName = GetPlayerName();
        if (participants.Count > 0)
        {
            AddSystemTurn(
                participants,
                playerName + " returned and is once again present in the "
                + "conversation. " + playerName + " may now be addressed and "
                + "selected as the next speaker.");
        }

        DialogueInputActors.ClearAll();
        NeuralNPC active = NeuralNPC.currentActiveDialogNeuralNPC;
        active?.DoStartNPCMode(DialogBox.SpriteSwitchMode.Normal);
        Notify(playerName + " rejoined the conversation.");
    }

    internal static void ExitWithoutReturn()
    {
        if (!IsPlayerAway)
        {
            return;
        }

        IsPlayerAway = false;
        _lastActingNpc = null;
        DialogueInputActors.ClearAll();
    }

    private static List<NeuralNPC> GetParticipants()
    {
        return (NeuralNPC.multiDialogParticipants
                ?? new List<NeuralNPC>())
            .Where(npc => npc != null)
            .Distinct()
            .ToList();
    }

    private static void AddSystemTurn(
        IEnumerable<NeuralNPC> participants,
        string text)
    {
        foreach (NeuralNPC participant in participants)
        {
            participant.dialogElements.AddToDialog(SpeakerType.System, text);
        }
    }

    private static string GetPlayerName()
    {
        return Player.Instance != null
            ? Player.Instance.playerName
            : "The player";
    }

    private static void Notify(string message)
    {
        if (UpperNotificationUI.Instance != null)
        {
            UpperNotificationUI.Instance.OneOff(message);
        }
    }
}

[HarmonyPatch(typeof(NeuralNPC), "GetNextMultiSpeaker")]
internal static class AwaySpeakerSelectionPatch
{
    private static bool Prefix(
        NeuralNPC speaker,
        ref Task<NeuralNPC> __result)
    {
        if (!ConversationObserverController.IsPlayerAway)
        {
            return true;
        }

        NeuralNPC nextSpeaker =
            ConversationObserverController.ChooseNextNpc(speaker);
        if (nextSpeaker == null)
        {
            ConversationObserverController.Rejoin();
        }
        __result = Task.FromResult(nextSpeaker);
        return false;
    }
}

[HarmonyPatch(typeof(DialogBox), nameof(DialogBox.StartNPCMode))]
internal static class AwayPortraitPatch
{
    private static void Prefix(
        ref Sprite playerSprite,
        ref float playerSpriteScale,
        ref Vector2 playerSpriteOffset)
    {
        if (!ConversationObserverController.TryGetCounterpart(
                out NeuralNPC counterpart))
        {
            return;
        }

        playerSprite = counterpart.GetDialogSprite();
        playerSpriteScale = counterpart.dialogSpriteScale;
        playerSpriteOffset = counterpart.dialogSpriteOffset;
    }
}

[HarmonyPatch(typeof(DialogBox), nameof(DialogBox.SendInput))]
internal static class AwayActingInputPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix()
    {
        return !ConversationObserverController.TrySubmitActingInput();
    }
}

[HarmonyPatch(typeof(DialogBox), "Update")]
internal static class AwayRuntimeUpdatePatch
{
    private static void Postfix()
    {
        ConversationObserverController.UpdateActingInput();
    }
}

[HarmonyPatch(typeof(DialogBox), nameof(DialogBox.StopContinueOnlyMode))]
internal static class AwayInterruptPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        ConversationObserverController.Rejoin();
    }
}

[HarmonyPatch(typeof(DialogBox), nameof(DialogBox.EndDialog))]
internal static class AwayEndDialogPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        ConversationObserverController.ExitWithoutReturn();
    }
}

[HarmonyPatch(typeof(DialogBox), nameof(DialogBox.CloseBox))]
internal static class AwayCloseBoxPatch
{
    private static void Postfix()
    {
        ConversationObserverController.ExitWithoutReturn();
    }
}
