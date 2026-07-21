#pragma once
#include <stdint.h>

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
    int       mesh_index;       /* index into the file's meshes (CgltfMultiMeshData.mesh_names) */
} CgltfMeshData;

/* Name of one glTF mesh (a mesh can own several primitives / CgltfMeshData). */
typedef struct {
    char name[128];
} CgltfMeshName;

/* One CgltfMeshData per primitive, across every mesh in the file. */
typedef struct {
    CgltfMeshData*  primitives;
    uint32_t        primitive_count;
    CgltfMeshName*  mesh_names;   /* one per glTF mesh, indexed by mesh_index */
    uint32_t        mesh_count;
} CgltfMultiMeshData;

/* Load every mesh/primitive from a .gltf or .glb file. Primitives without a
   POSITION attribute are skipped. Returns 0 on success (at least one
   primitive loaded), nonzero on parse failure or if no primitive could be
   loaded. Call cgltf_wrap_free_all() when done. */
int cgltf_wrap_load_all(const char* path, CgltfMultiMeshData* out);
void cgltf_wrap_free_all(CgltfMultiMeshData* out);

/* ── Node hierarchy ───────────────────────────────────────────────────────── */

/* One glTF node: local transform (already resolved to TRS, decomposing the
   rare has_matrix case) plus its parent and mesh, both as flat-array indices. */
typedef struct {
    char    name[128];
    int32_t parent_index;   /* index into CgltfNodeHierarchy.nodes, or -1 for a root */
    int32_t mesh_index;     /* index into the file's meshes, or -1 (no mesh) */
    float   translation[3];
    float   rotation[4];    /* quaternion, xyzw */
    float   scale[3];
} CgltfNodeData;

typedef struct {
    CgltfNodeData* nodes;
    uint32_t       node_count;
} CgltfNodeHierarchy;

/* Load every node in a .gltf or .glb file's flat node array (not just those
   reachable from the default scene) with its local TRS transform, parent, and
   mesh index. Returns 0 on success (at least one node), nonzero on parse
   failure or an empty node array. Call cgltf_wrap_free_hierarchy() when done. */
int cgltf_wrap_load_hierarchy(const char* path, CgltfNodeHierarchy* out);
void cgltf_wrap_free_hierarchy(CgltfNodeHierarchy* out);

/* ── Materials & images ──────────────────────────────────────────────────────
   A separate "model info" pass exposes a glTF file's materials and images so an
   importer can convert a single source model into multiple engine assets
   (materials + textures). Geometry is loaded separately via cgltf_wrap_load_all. */

/* Reference from a material slot to one of the model's images. */
typedef struct {
    int      has_texture;   /* 1 when this slot binds a texture */
    int      image_index;   /* index into CgltfModelData.images, or -1 */
    int      uv_set;        /* texcoord set (TEXCOORD_n), usually 0 */
} CgltfTexRef;

/* glTF alpha rendering mode. */
enum {
    CGLTF_WRAP_ALPHA_OPAQUE = 0,
    CGLTF_WRAP_ALPHA_MASK   = 1,
    CGLTF_WRAP_ALPHA_BLEND  = 2,
};

/* One glTF material flattened to the metallic-roughness model. */
typedef struct {
    char        name[128];
    float       base_color[4];      /* baseColorFactor (rgba) */
    float       metallic;           /* metallicFactor */
    float       roughness;          /* roughnessFactor */
    float       emissive[3];        /* emissiveFactor (rgb) */
    float       emissive_strength;  /* KHR_materials_emissive_strength, default 1 */
    float       normal_scale;       /* normalTexture.scale */
    float       occlusion_strength; /* occlusionTexture.strength */
    int         alpha_mode;         /* CGLTF_WRAP_ALPHA_* */
    float       alpha_cutoff;       /* alphaCutoff */
    int         double_sided;       /* 1 when doubleSided */
    CgltfTexRef albedo;             /* baseColorTexture */
    CgltfTexRef metallic_roughness; /* metallicRoughnessTexture */
    CgltfTexRef normal;             /* normalTexture */
    CgltfTexRef emissive_tex;       /* emissiveTexture */
    CgltfTexRef occlusion;          /* occlusionTexture */
} CgltfMaterial;

/* One glTF image: either an external file (uri) or embedded bytes (data). */
typedef struct {
    char                 name[128];
    char                 uri[256];     /* external relative path; "" if embedded */
    char                 mime_type[32];/* e.g. "image/png" when embedded */
    const unsigned char* data;         /* embedded bytes; NULL when external */
    uint32_t             data_size;    /* embedded byte length */
} CgltfImage;

typedef struct {
    CgltfMaterial* materials;
    uint32_t       material_count;
    CgltfImage*    images;
    uint32_t       image_count;
    void*          _data;   /* opaque cgltf_data* kept alive for embedded bytes */
} CgltfModelData;

/* Load all materials and images from a .gltf or .glb file. Embedded image bytes
   (GLB bin chunk / base64 data URIs) are referenced in place, so the returned
   CgltfModelData must stay alive until cgltf_wrap_free_model() is called.
   Returns 0 on success. */
int cgltf_wrap_load_model(const char* path, CgltfModelData* out);
void cgltf_wrap_free_model(CgltfModelData* out);
