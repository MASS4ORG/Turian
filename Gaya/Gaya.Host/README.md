# Gaya Host

The desktop workbench for Gaya plugins. Provides plugin activation, docking, layout persistence, the Settings panel and application services through the [SDK](../Gaya.Sdk/).

Turian Studio composes this host with its editor plugin. The host has no Turian dependency.

The host registers the user-level `gaya.appearance` page for theme, text size, zoom and native window
decorations. `Workbench.Appearance` and the registered `AppearanceSettings` service refer to that
page. The Settings theme dropdown and View/Themes menu share the persisted choice. Applications
moving an existing appearance page can pass its id as `previousAppearancePageId` to `PluginHost.Load`.
