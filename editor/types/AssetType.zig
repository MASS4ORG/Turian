/// Broad asset category derived from file extension.
pub const AssetType = enum(u8) {
    unknown,
    script,
    image,
    audio,
    model,
    scene,
    material,
    data_asset,
    input_actions,
    project_settings,
    ui_document,
};
