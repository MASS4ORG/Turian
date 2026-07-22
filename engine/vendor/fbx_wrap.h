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

/* ── Node hierarchy ───────────────────────────────────────────────────────────
   Unlike fbx_wrap_load_all (which bakes every node INSTANCE's geometry into
   world space, so a mesh referenced by many nodes is baked once per node),
   this pair loads each unique mesh datablock's geometry exactly once, in
   local space, plus the node tree so an importer can place instances via
   their own transform -- mirrors cgltf_wrap's load_hierarchy/load_all split. */

/* One (unique mesh, material slot) chunk of triangulated, LOCAL-space FBX
   geometry: only the mesh's own geometric offset (FBX's separate "geometric
   transform" concept, baked via the first node instance's geometry_to_node)
   is applied -- never a node's placement in the scene. Multiple node
   instances of the same mesh share these chunks; per-instance placement is
   the importer's job via the paired node hierarchy. */
typedef struct {
    float*    positions;    /* vertex_count * 3 */
    float*    normals;      /* vertex_count * 3, may be NULL */
    float*    uvs;          /* vertex_count * 2, may be NULL */
    uint32_t* indices;
    uint32_t  vertex_count;
    uint32_t  index_count;
    int       has_normals;
    int       has_uvs;
    int       material_index; /* index into the model's materials, or -1 */
    int       mesh_index;     /* index into FbxMultiMeshLocalData's unique meshes */
} FbxMeshLocalData;

/* Name of one unique FBX mesh datablock (can be shared by several nodes). */
typedef struct {
    char name[128];
} FbxMeshName;

/* One FbxMeshLocalData per (unique mesh, material slot), in mesh-major order
   (all of mesh 0's parts, then mesh 1's, ...) across every unique mesh
   datablock in the scene -- not every node instance. */
typedef struct {
    FbxMeshLocalData* primitives;
    uint32_t          primitive_count;
    FbxMeshName*      mesh_names;  /* one per unique mesh, indexed by mesh_index */
    uint32_t          mesh_count;
} FbxMultiMeshLocalData;

/* Load every unique mesh datablock's geometry once, in local space. Material
   assignment uses the mesh's own default materials (ufbx_mesh.materials),
   not any per-node override -- matches glTF, which has no such concept.
   Returns 0 on success (at least one chunk loaded). Call
   fbx_wrap_free_meshes() when done. */
int fbx_wrap_load_meshes(const char* path, FbxMultiMeshLocalData* out);
void fbx_wrap_free_meshes(FbxMultiMeshLocalData* out);

/* One FBX node: local transform (ufbx already decomposes this to TRS, no
   matrix case to handle) plus its parent and mesh, both as flat-array
   indices matching FbxMultiMeshLocalData's ordering. */
typedef struct {
    char    name[128];
    int32_t parent_index; /* index into FbxNodeHierarchy.nodes, or -1 for a root */
    int32_t mesh_index;   /* index into FbxMultiMeshLocalData's unique meshes, or -1 */
    float   translation[3];
    float   rotation[4];  /* quaternion, xyzw */
    float   scale[3];
} FbxNodeData;

typedef struct {
    FbxNodeData* nodes;
    uint32_t     node_count;
} FbxNodeHierarchy;

/* Load every node in an FBX file's flat node array -- including ufbx's
   synthetic root node, which becomes this hierarchy's single tree root --
   with its local TRS transform, parent, and mesh index. Returns 0 on success
   (at least one node), nonzero on parse failure or an empty node array. Call
   fbx_wrap_free_hierarchy() when done. */
int fbx_wrap_load_hierarchy(const char* path, FbxNodeHierarchy* out);
void fbx_wrap_free_hierarchy(FbxNodeHierarchy* out);

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
