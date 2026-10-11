# Turian Engine

Runtime libraries for scenes, components, assets, input and simulation.

- [Core](Core/): engine runtime, service contracts and the headless player; no GPU or window dependency.
- [Attributes](Attributes/): markers used by serializers, analyzers and generated code.

The Vulkan renderer, window and window input ship as the [`org.mass4.turian.vulkan`](../Packages/org.mass4.turian.vulkan/) brick, which every project uses unless it opts out. Other optional features, including camera rigs and in-game UI, also ship as [built-in bricks](../Packages/).
