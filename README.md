# Conversation Observer

A BepInEx plugin for Silverpine that adds a Modding Tools conversation action
named **Step Away** whenever at least two NPCs are participating.

## Use

1. Start a multi-NPC conversation.
2. Open the conversation action menu and choose **Step Away**.
3. Use **Continue** to let the NPCs speak to each other. The player is removed
   from next-speaker selection and from the scene instructions.
4. Choose **Interrupt** to bring the player back.

Automatic NPC turns include hidden system boundaries so Silverpine's native
dialogue-history pruning remains valid during long NPC-only exchanges.

While away, a Modding Tools prompt transform begins the generated conversation
context at the departure notice, removes player-authored turns and
player-specific prompt scaffolding, and changes Silverpine's normal
second-person player POV to third-person narration centered on the NPCs still
present. These changes affect prompt copies only; **Interrupt** restores normal
prompt construction without altering the stored conversation history.

While the player is away, the player portrait slot shows a different active
participant from the NPC in the normal NPC slot. The two slots never select the
same NPC. Custom-NPC portraits use their original player-side scale and offset
metadata instead of their mirrored NPC-side coordinates.

## Optional impersonation support

Conversation Observer does not depend on an impersonation add-on. Add-ons may
register an NPC identity through Modding Tools' `DialogueInputActors` hook. If
one is active, the Continue control becomes **Speak as NPC Name** and accepts a
typed NPC turn without treating it as player input or returning the player to
the conversation. The impersonated NPC is also preferred for the player-side
portrait; if that NPC is already the active speaker, another NPC is shown so
the portrait slots never duplicate one another. **Interrupt** remains the only
way to rejoin.

Salt Dialogue Impersonator 1.2.1 registers this optional hook.

## Credits

Created by **Saelac and ChatGPT**.
