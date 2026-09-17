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

Stepping away preserves the full available conversation history in the prompt,
including earlier player, NPC, and system turns. The departure notice establishes
that the player is no longer present without discarding the prior conversation.
Silverpine's normal context limits, memory compression, and memory retrieval
still apply. Compressed memories and lore are preserved, including references
to earlier interactions with the player.

While away, a Modding Tools text transform removes player-specific scaffolding
from the current environment section and changes Silverpine's normal
second-person player POV to third-person narration centered on the NPCs still
present. Historical dialogue entries are neither filtered nor rewritten by this
transform. **Interrupt** restores normal prompt construction.

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
