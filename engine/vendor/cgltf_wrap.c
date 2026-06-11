#define CGLTF_IMPLEMENTATION
#include "cgltf.h"
#include "cgltf_wrap.h"
#include <stdlib.h>
#include <string.h>

int cgltf_wrap_load(const char* path, CgltfMeshData* out) {
    cgltf_options opts;
    memset(&opts, 0, sizeof(opts));
    cgltf_data* data = NULL;

    if (cgltf_parse_file(&opts, path, &data) != cgltf_result_success) return 1;
    if (cgltf_load_buffers(&opts, data, path) != cgltf_result_success) {
        cgltf_free(data);
        return 2;
    }
    if (data->meshes_count == 0 || data->meshes[0].primitives_count == 0) {
        cgltf_free(data);
        return 3;
    }

    cgltf_primitive* prim = &data->meshes[0].primitives[0];

    cgltf_accessor* pos_acc  = NULL;
    cgltf_accessor* norm_acc = NULL;
    cgltf_accessor* uv_acc   = NULL;

    for (cgltf_size ai = 0; ai < prim->attributes_count; ai++) {
        cgltf_attribute* attr = &prim->attributes[ai];
        if (attr->type == cgltf_attribute_type_position)
            pos_acc = attr->data;
        else if (attr->type == cgltf_attribute_type_normal)
            norm_acc = attr->data;
        else if (attr->type == cgltf_attribute_type_texcoord && attr->index == 0)
            uv_acc = attr->data;
    }

    if (!pos_acc) { cgltf_free(data); return 4; }

    uint32_t vcount = (uint32_t)pos_acc->count;
    uint32_t icount = prim->indices ? (uint32_t)prim->indices->count : vcount;

    float*    positions = (float*)malloc(vcount * 3 * sizeof(float));
    float*    normals   = norm_acc ? (float*)malloc(vcount * 3 * sizeof(float)) : NULL;
    float*    uvs       = uv_acc   ? (float*)malloc(vcount * 2 * sizeof(float)) : NULL;
    uint32_t* indices   = (uint32_t*)malloc(icount * sizeof(uint32_t));

    if (!positions || !indices) {
        free(positions); free(normals); free(uvs); free(indices);
        cgltf_free(data);
        return 5;
    }

    for (uint32_t i = 0; i < vcount; i++)
        cgltf_accessor_read_float(pos_acc, i, &positions[i * 3], 3);
    if (norm_acc)
        for (uint32_t i = 0; i < vcount; i++)
            cgltf_accessor_read_float(norm_acc, i, &normals[i * 3], 3);
    if (uv_acc)
        for (uint32_t i = 0; i < vcount; i++)
            cgltf_accessor_read_float(uv_acc, i, &uvs[i * 2], 2);

    if (prim->indices) {
        for (uint32_t i = 0; i < icount; i++)
            indices[i] = (uint32_t)cgltf_accessor_read_index(prim->indices, i);
    } else {
        for (uint32_t i = 0; i < icount; i++) indices[i] = i;
    }

    cgltf_free(data);

    out->positions    = positions;
    out->normals      = normals;
    out->uvs          = uvs;
    out->indices      = indices;
    out->vertex_count = vcount;
    out->index_count  = icount;
    out->has_normals  = normals ? 1 : 0;
    out->has_uvs      = uvs    ? 1 : 0;
    return 0;
}

void cgltf_wrap_free(CgltfMeshData* data) {
    if (!data) return;
    free(data->positions);
    free(data->normals);
    free(data->uvs);
    free(data->indices);
    data->positions = NULL;
    data->normals   = NULL;
    data->uvs       = NULL;
    data->indices   = NULL;
}
