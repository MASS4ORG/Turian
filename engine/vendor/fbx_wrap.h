#pragma once
#include <stdint.h>

/* One (node instance, material slot) chunk of triangulated, world-space FBX
   geometry. Positions/normals are baked with the owning node's world
   transform, so chunks from different nodes can be concatenated directly. */
typedef struct {
    float*    positions;    /* vertex_count * 3 */
    float*    normals;      /* vertex_count * 3, may be NULL */
    float*    uvs;          /* vertex_count * 2, may be NULL */
    uint32_t* indices;
    uint32_t  vertex_count;
    uint32_t  index_count;
    int       has_normals;
    int       has_uvs;
    int       material_index;   /* index into the model's materials, or -1 */
} FbxMeshData;

/* One FbxMeshData per (node instance, material slot) used across the scene. */
typedef struct {
    FbxMeshData* primitives;
    uint32_t     primitive_count;
} FbxMultiMeshData;

/* Load every mesh instance in an FBX (binary or ASCII) file, triangulated and
   baked into world space via each node's transform. Axis/unit convention is
   normalized to right-handed Y-up, meters. Returns 0 on success (at least one
   chunk loaded), nonzero on parse failure or if nothing could be loaded.
   Call fbx_wrap_free_all() when done. */
int fbx_wrap_load_all(const char* path, FbxMultiMeshData* out);
void fbx_wrap_free_all(FbxMultiMeshData* out);

/* ── Materials & images ──────────────────────────────────────────────────────
   Mirrors cgltf_wrap's model-info pass: a separate query exposes an FBX
   file's materials and textures so an importer can generate engine material/
   texture assets. Geometry is loaded separately via fbx_wrap_load_all. */

typedef struct {
    int      has_texture;
    int      image_index;   /* index into FbxModelData.images, or -1 */
    int      uv_set;        /* always 0: ufbx normalizes to the first UV set */
} FbxTexRef;

enum {
    FBX_WRAP_ALPHA_OPAQUE = 0,
    FBX_WRAP_ALPHA_MASK   = 1,
    FBX_WRAP_ALPHA_BLEND  = 2,
};

/* One FBX material, best-effort mapped to the metallic-roughness model via
   ufbx's shading-model-agnostic `pbr` maps (works for Lambert/Phong/PBR
   materials alike). FBX has no packed metallic-roughness texture (unlike
   glTF), so `metallic_roughness` and `occlusion` never bind a texture --
   only their scalar factors are populated. */
typedef struct {
    char        name[128];
    float       base_color[4];
    float       metallic;
    float       roughness;
    float       emissive[3];
    float       emissive_strength;
    float       normal_scale;
    float       occlusion_strength;
    int         alpha_mode;
    float       alpha_cutoff;
    int         double_sided;
    FbxTexRef   albedo;
    FbxTexRef   metallic_roughness; /* never bound: see comment above */
    FbxTexRef   normal;
    FbxTexRef   emissive_tex;
    FbxTexRef   occlusion;          /* never bound: see comment above */
} FbxMaterial;

/* One FBX texture: either an external file (uri) or embedded bytes (data). */
typedef struct {
    char                 name[128];
    char                 uri[256];      /* external relative path; "" if embedded */
    char                 mime_type[32]; /* guessed from filename extension */
    const unsigned char* data;          /* embedded bytes; NULL when external */
    uint32_t             data_size;
} FbxImage;

typedef struct {
    FbxMaterial* materials;
    uint32_t     material_count;
    FbxImage*    images;
    uint32_t     image_count;
    void*        _scene; /* opaque ufbx_scene* kept alive for embedded bytes */
} FbxModelData;

/* Load all materials and textures from an FBX file. Embedded texture bytes
   are referenced in place, so the returned FbxModelData must stay alive
   until fbx_wrap_free_model() is called. Returns 0 on success. */
int fbx_wrap_load_model(const char* path, FbxModelData* out);
void fbx_wrap_free_model(FbxModelData* out);
