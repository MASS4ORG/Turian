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
} CgltfMeshData;

/* Load first mesh, first primitive from a .gltf or .glb file.
   Returns 0 on success. Call cgltf_wrap_free() when done. */
int cgltf_wrap_load(const char* path, CgltfMeshData* out);
void cgltf_wrap_free(CgltfMeshData* data);
