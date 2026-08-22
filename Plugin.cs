using BepInEx;
using HarmonyLib;
using Silverpine.ModdingTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ConversationObserver;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    "1.9.3")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "salt.silverpine.conversationobserver";
    public const string PluginName = "Conversation Observer";
    public const string PluginVersion = "1.0.4";

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
    private const float PortraitSideOffsetX = 177f;
    private const string StepAwayActionId =
        Plugin.PluginGuid + ".step-away";
    private const string AwayPromptTransformId =
        Plugin.PluginGuid + ".away-prompt";
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
        DialoguePromptTransforms.Register(
            Plugin.PluginGuid,
            new DialoguePromptTransformDefinition
            {
                Id = AwayPromptTransformId,
                Order = -1000,
                IsActive = _ => IsPlayerAway,
                TransformHistory = FilterAwayPromptHistory,
                TransformText = TransformAwayPromptText
            });
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

    internal static void RecordAwayContinuationBoundary(
        NeuralNPC nextSpeaker)
    {
        if (!IsPlayerAway || nextSpeaker == null)
        {
            return;
        }

        AddSystemTurn(
            GetParticipants(),
            "Only the NPCs still present may speak. Next speaker: "
            + nextSpeaker.GetFinalName() + ".");
    }

    private static IEnumerable<NeuralNPC.DialogElement>
        FilterAwayPromptHistory(
            DialoguePromptContext context,
            IReadOnlyList<NeuralNPC.DialogElement> history)
    {
        string playerName = context.Player != null
            ? context.Player.playerName
            : GetPlayerName();
        string departurePrefix = playerName
            + " stepped away and is no longer present in this conversation.";
        int departureIndex = -1;
        for (int index = history.Count - 1; index >= 0; index--)
        {
            NeuralNPC.DialogElement element = history[index];
            if (element.speakerType == SpeakerType.System &&
                element.contents.StartsWith(
                    departurePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                departureIndex = index;
                break;
            }
        }

        if (departureIndex < 0)
        {
            return history.Where(element =>
                element.speakerType != SpeakerType.Player);
        }

        var filtered = new List<NeuralNPC.DialogElement>();
        for (int index = departureIndex; index < history.Count; index++)
        {
            NeuralNPC.DialogElement element = history[index];
            if (element.speakerType == SpeakerType.Player)
            {
                continue;
            }
            if (index == departureIndex)
            {
                filtered.Add(element);
                continue;
            }
            if (element.speakerType == SpeakerType.System &&
                TrySanitizeImpersonatedTurn(element, out string sanitized))
            {
                filtered.Add(new NeuralNPC.DialogElement(
                    SpeakerType.System,
                    sanitized,
                    element.turnCount));
                continue;
            }
            if (element.speakerType == SpeakerType.System &&
                ContainsPlayerScaffolding(element.contents, playerName))
            {
                continue;
            }
            filtered.Add(element);
        }
        return filtered;
    }

    private static bool TrySanitizeImpersonatedTurn(
        NeuralNPC.DialogElement element,
        out string sanitized)
    {
        const string prefix =
            "The preceding line was directly spoken in-scene by ";
        sanitized = "";
        if (!element.contents.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int actorEnd = element.contents.IndexOf(
            ", not by ",
            prefix.Length,
            StringComparison.OrdinalIgnoreCase);
        if (actorEnd <= prefix.Length)
        {
            return false;
        }

        string actorName = element.contents.Substring(
            prefix.Length,
            actorEnd - prefix.Length);
        sanitized = prefix + actorName
            + ". Treat it as authoritative dialogue from "
            + actorName + ".";
        return true;
    }

    private static string TransformAwayPromptText(
        DialoguePromptContext context,
        DialoguePromptTextSection section,
        string text)
    {
        string playerName = context.Player != null
            ? context.Player.playerName
            : GetPlayerName();
        if (section == DialoguePromptTextSection.WorldLore)
        {
            return RemovePlayerParagraphs(text, playerName);
        }

        string result = RemovePlayerCharacterBlock(
            text,
            playerName,
            context.Npc.GetFinalName());
        result = result.Replace(
            "POV: Write in second person present tense from "
            + playerName + "'s point of view.",
            "POV: Write in third person present tense, centered on the NPCs "
            + "who are still present.");
        result = string.Join(
            "\n",
            result.Replace("\r\n", "\n")
                .Split('\n')
                .Where(line =>
                    !ContainsPlayerScaffolding(line, playerName)));
        return Regex.Replace(result, "\n{3,}", "\n\n");
    }

    private static string RemovePlayerCharacterBlock(
        string text,
        string playerName,
        string npcName)
    {
        string startMarker =
            "\n\nThis is the character description of " + playerName
            + ", the player's character:\n";
        string endMarker =
            "\n\nThis is the character description of " + npcName + ":\n";
        int start = text.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }
        int end = text.IndexOf(
            endMarker,
            start + startMarker.Length,
            StringComparison.Ordinal);
        return end >= 0 ? text.Remove(start, end - start) : text;
    }

    private static string RemovePlayerParagraphs(
        string text,
        string playerName)
    {
        string[] paragraphs = Regex.Split(
            text.Replace("\r\n", "\n"),
            "\n{2,}");
        return string.Join(
            "\n\n",
            paragraphs.Where(paragraph =>
                !ContainsPlayerScaffolding(paragraph, playerName)));
    }

    private static bool ContainsPlayerScaffolding(
        string text,
        string playerName)
    {
        return text.IndexOf(
                   playerName,
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf(
                   "the player",
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf(
                   "player's character",
                   StringComparison.OrdinalIgnoreCase) >= 0;
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

    internal static void GetPlayerSidePortraitPlacement(
        NeuralNPC npc,
        out float scale,
        out Vector2 offset)
    {
        if (TryGetPlayerCharacterAssets(npc, out CharacterAssetPack assets))
        {
            scale = assets.dialogSpriteScale;
            offset = new Vector2(
                assets.dialogSpriteOffsetX,
                assets.dialogSpriteOffsetY);
            return;
        }

        scale = npc.dialogSpriteScale;
        offset = new Vector2(
            PortraitSideOffsetX - npc.dialogSpriteOffset.x,
            npc.dialogSpriteOffset.y);
    }

    private static bool TryGetPlayerCharacterAssets(
        NeuralNPC npc,
        out CharacterAssetPack assets)
    {
        assets = null;
        if (npc == null)
        {
            return false;
        }

        CustomNPCHandler handler = npc.GetComponent<CustomNPCHandler>();
        string definitionName = handler != null
            ? handler.customContentName
            : npc.GetFinalName();
        return !string.IsNullOrWhiteSpace(definitionName)
            && CustomContentDefinition_NPC.loaded.TryGetValue(
                definitionName,
                out CustomContentDefinition_NPC npcDefinition)
            && npcDefinition.enabled
            && !string.IsNullOrWhiteSpace(
                npcDefinition.customPlayerCharacterDefinitionName)
            && CustomContentDefinition_PlayerCharacter.loaded.TryGetValue(
                npcDefinition.customPlayerCharacterDefinitionName,
                out CustomContentDefinition_PlayerCharacter playerDefinition)
            && (assets = playerDefinition.assets) != null;
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
        else
        {
            // Silverpine's native history pruner expects a Player or System
            // entry to separate runs of NPC turns. Without this boundary, a
            // sufficiently long away-mode exchange can leave only dialog
            // markers and make its First(...) call throw.
            ConversationObserverController.RecordAwayContinuationBoundary(
                nextSpeaker);
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
        ConversationObserverController.GetPlayerSidePortraitPlacement(
            counterpart,
            out playerSpriteScale,
            out playerSpriteOffset);
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
