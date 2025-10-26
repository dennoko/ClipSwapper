# Clip Swapper (Animator Controller Generator)

An EditorWindow that lets you:

- Select a source Animator Controller
- See all Animation Clips used (with the layers that reference them)
- Filter the list by clip name or layer name (incremental)
- Assign replacement Animation Clips per item
- Generate a new Animator Controller asset (copied next to the original) with your replacements applied

## How to open

- In Unity, open the menu: `Tools > Clip Swapper` (opens as a dockable editor tab)

## How to use

1. Drag & drop (or select) your source `AnimatorController` in the field at the top.
2. Optionally type in the filter box to narrow by clip name or layer name.
3. For any clip you want to change, assign a replacement `AnimationClip` in the right column.
4. Enter a new controller name (defaults to `<OriginalName>_Override`).
5. Press `Generate` to create the new controller next to the original.

Notes:
- The tool traverses all layers, states, sub-state machines, and BlendTrees.
- Only motions that are `AnimationClip`s are swapped. Transitions and other settings are preserved.
- If no replacements are provided, the new controller will be an identical copy.
