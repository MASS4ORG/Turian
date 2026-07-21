#define CGLTF_IMPLEMENTATION
#include "cgltf.h"
#include "cgltf_wrap.h"
#include <stdlib.h>
#include <string.h>
#include <math.h>

/* Extracts one primitive's geometry into `out`. Returns 0 on success, nonzero
   if the primitive has no POSITION attribute or an allocation fails. */
static int load_primitive(cgltf_data* data, cgltf_primitive* prim, int mesh_index, CgltfMeshData* out) {
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

    if (!pos_acc) return 4;

    uint32_t vcount = (uint32_t)pos_acc->count;
    uint32_t icount = prim->indices ? (uint32_t)prim->indices->count : vcount;

    float*    positions = (float*)malloc(vcount * 3 * sizeof(float));
    float*    normals   = norm_acc ? (float*)malloc(vcount * 3 * sizeof(float)) : NULL;
    float*    uvs       = uv_acc   ? (float*)malloc(vcount * 2 * sizeof(float)) : NULL;
    uint32_t* indices   = (uint32_t*)malloc(icount * sizeof(uint32_t));

    if (!positions || !indices) {
        free(positions); free(normals); free(uvs); free(indices);
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

    int material_index = -1;
    if (prim->material && data->materials_count > 0)
        material_index = (int)(prim->material - data->materials);

    out->positions      = positions;
    out->normals        = normals;
    out->uvs            = uvs;
    out->indices        = indices;
    out->vertex_count   = vcount;
    out->index_count    = icount;
    out->has_normals    = normals ? 1 : 0;
    out->has_uvs        = uvs    ? 1 : 0;
    out->material_index = material_index;
    out->mesh_index     = mesh_index;
    return 0;
}

static void copy_str(char* dst, size_t cap, const char* src) {
    if (cap == 0) return;
    if (!src) { dst[0] = 0; return; }
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = 0;
}

static void free_primitive(CgltfMeshData* data) {
    free(data->positions);
    free(data->normals);
    free(data->uvs);
    free(data->indices);
}

int cgltf_wrap_load_all(const char* path, CgltfMultiMeshData* out) {
    memset(out, 0, sizeof(*out));

    cgltf_options opts;
    memset(&opts, 0, sizeof(opts));
    cgltf_data* data = NULL;

    if (cgltf_parse_file(&opts, path, &data) != cgltf_result_success) return 1;
    if (cgltf_load_buffers(&opts, data, path) != cgltf_result_success) {
        cgltf_free(data);
        return 2;
    }

    uint32_t total = 0;
    for (cgltf_size mi = 0; mi < data->meshes_count; mi++)
        total += (uint32_t)data->meshes[mi].primitives_count;
    if (total == 0) {
        cgltf_free(data);
        return 3;
    }

    CgltfMeshData* prims = (CgltfMeshData*)calloc(total, sizeof(CgltfMeshData));
    CgltfMeshName* names = data->meshes_count ? (CgltfMeshName*)calloc(data->meshes_count, sizeof(CgltfMeshName)) : NULL;
    if (!prims || (data->meshes_count && !names)) {
        free(prims); free(names);
        cgltf_free(data);
        return 5;
    }

    for (cgltf_size mi = 0; mi < data->meshes_count; mi++)
        copy_str(names[mi].name, sizeof(names[mi].name), data->meshes[mi].name);

    uint32_t count = 0;
    for (cgltf_size mi = 0; mi < data->meshes_count; mi++) {
        cgltf_mesh* mesh = &data->meshes[mi];
        for (cgltf_size pi = 0; pi < mesh->primitives_count; pi++) {
            if (load_primitive(data, &mesh->primitives[pi], (int)mi, &prims[count]) == 0)
                count++;
        }
    }

    cgltf_free(data);

    if (count == 0) {
        free(prims);
        free(names);
        return 4;
    }

    out->primitives = prims;
    out->primitive_count = count;
    out->mesh_names = names;
    out->mesh_count = (uint32_t)data->meshes_count;
    return 0;
}

void cgltf_wrap_free_all(CgltfMultiMeshData* out) {
    if (!out || !out->primitives) return;
    for (uint32_t i = 0; i < out->primitive_count; i++)
        free_primitive(&out->primitives[i]);
    free(out->primitives);
    free(out->mesh_names);
    out->primitives = NULL;
    out->primitive_count = 0;
    out->mesh_names = NULL;
    out->mesh_count = 0;
}

/* ── Materials & images ──────────────────────────────────────────────────── */

/* Resolve the backing image of a texture, preferring the standard image and
   falling back to the KHR_texture_basisu / EXT_texture_webp variants. */
static cgltf_image* resolve_image(cgltf_texture* tex) {
    if (!tex) return NULL;
    if (tex->image) return tex->image;
    if (tex->has_basisu && tex->basisu_image) return tex->basisu_image;
    if (tex->has_webp && tex->webp_image) return tex->webp_image;
    return NULL;
}

static void fill_texref(CgltfTexRef* ref, const cgltf_texture_view* view, cgltf_data* data) {
    ref->has_texture = 0;
    ref->image_index = -1;
    ref->uv_set = 0;
    if (!view || !view->texture) return;
    cgltf_image* img = resolve_image(view->texture);
    if (!img || data->images_count == 0) return;
    ref->has_texture = 1;
    ref->image_index = (int)(img - data->images);
    ref->uv_set = (int)view->texcoord;
}

int cgltf_wrap_load_model(const char* path, CgltfModelData* out) {
    memset(out, 0, sizeof(*out));

    cgltf_options opts;
    memset(&opts, 0, sizeof(opts));
    cgltf_data* data = NULL;

    if (cgltf_parse_file(&opts, path, &data) != cgltf_result_success) return 1;
    /* Buffers are needed so GLB / data-URI embedded image bytes are available. */
    if (cgltf_load_buffers(&opts, data, path) != cgltf_result_success) {
        cgltf_free(data);
        return 2;
    }

    uint32_t mat_count = (uint32_t)data->materials_count;
    uint32_t img_count = (uint32_t)data->images_count;

    CgltfMaterial* mats = mat_count ? (CgltfMaterial*)calloc(mat_count, sizeof(CgltfMaterial)) : NULL;
    CgltfImage*    imgs = img_count ? (CgltfImage*)calloc(img_count, sizeof(CgltfImage)) : NULL;

    if ((mat_count && !mats) || (img_count && !imgs)) {
        free(mats); free(imgs);
        cgltf_free(data);
        return 5;
    }

    for (uint32_t i = 0; i < mat_count; i++) {
        cgltf_material* m = &data->materials[i];
        CgltfMaterial* o = &mats[i];

        copy_str(o->name, sizeof(o->name), m->name);

        /* Sensible defaults for the metallic-roughness model. */
        o->base_color[0] = o->base_color[1] = o->base_color[2] = o->base_color[3] = 1.0f;
        o->metallic = 1.0f;
        o->roughness = 1.0f;
        o->normal_scale = 1.0f;
        o->occlusion_strength = 1.0f;
        o->emissive_strength = 1.0f;
        o->alpha_cutoff = 0.5f;

        if (m->has_pbr_metallic_roughness) {
            cgltf_pbr_metallic_roughness* p = &m->pbr_metallic_roughness;
            memcpy(o->base_color, p->base_color_factor, sizeof(o->base_color));
            o->metallic = p->metallic_factor;
            o->roughness = p->roughness_factor;
            fill_texref(&o->albedo, &p->base_color_texture, data);
            fill_texref(&o->metallic_roughness, &p->metallic_roughness_texture, data);
        }

        o->emissive[0] = m->emissive_factor[0];
        o->emissive[1] = m->emissive_factor[1];
        o->emissive[2] = m->emissive_factor[2];
        if (m->has_emissive_strength)
            o->emissive_strength = m->emissive_strength.emissive_strength;

        o->normal_scale = m->normal_texture.scale != 0.0f ? m->normal_texture.scale : 1.0f;
        o->occlusion_strength = m->occlusion_texture.scale != 0.0f ? m->occlusion_texture.scale : 1.0f;

        switch (m->alpha_mode) {
            case cgltf_alpha_mode_mask:  o->alpha_mode = CGLTF_WRAP_ALPHA_MASK;  break;
            case cgltf_alpha_mode_blend: o->alpha_mode = CGLTF_WRAP_ALPHA_BLEND; break;
            default:                     o->alpha_mode = CGLTF_WRAP_ALPHA_OPAQUE; break;
        }
        o->alpha_cutoff = m->alpha_cutoff;
        o->double_sided = m->double_sided ? 1 : 0;

        fill_texref(&o->normal, &m->normal_texture, data);
        fill_texref(&o->emissive_tex, &m->emissive_texture, data);
        fill_texref(&o->occlusion, &m->occlusion_texture, data);
    }

    for (uint32_t i = 0; i < img_count; i++) {
        cgltf_image* im = &data->images[i];
        CgltfImage* o = &imgs[i];

        copy_str(o->name, sizeof(o->name), im->name);
        copy_str(o->mime_type, sizeof(o->mime_type), im->mime_type);

        if (im->buffer_view) {
            /* Embedded (GLB bin chunk or base64 data URI decoded into a buffer). */
            cgltf_buffer_view* bv = im->buffer_view;
            const unsigned char* base =
                bv->data ? (const unsigned char*)bv->data
                         : (bv->buffer && bv->buffer->data
                                ? (const unsigned char*)bv->buffer->data + bv->offset
                                : NULL);
            if (base) {
                o->data = base;
                o->data_size = (uint32_t)bv->size;
            }
        } else if (im->uri && strncmp(im->uri, "data:", 5) != 0) {
            /* External file. Copy + percent-decode the relative path. */
            copy_str(o->uri, sizeof(o->uri), im->uri);
            cgltf_decode_uri(o->uri);
        }
        /* data: URI images without a buffer_view are left empty (unsupported). */
    }

    out->materials      = mats;
    out->material_count = mat_count;
    out->images         = imgs;
    out->image_count    = img_count;
    out->_data          = data; /* keep alive for embedded image byte pointers */
    return 0;
}

void cgltf_wrap_free_model(CgltfModelData* out) {
    if (!out) return;
    free(out->materials);
    free(out->images);
    if (out->_data) cgltf_free((cgltf_data*)out->_data);
    out->materials = NULL;
    out->images = NULL;
    out->_data = NULL;
    out->material_count = 0;
    out->image_count = 0;
}

/* ── Node hierarchy ───────────────────────────────────────────────────────── */

/* Decompose a column-major 4x4 TRS matrix (glTF's `matrix` node property) into
   translation, rotation (quaternion), and scale. Used only for the rare node
   that specifies `matrix` instead of separate translation/rotation/scale.
   Degenerate (near-zero) scale axes fall back to an identity basis vector to
   avoid dividing by zero; mirrored (negative-determinant) matrices are not
   specially handled since real-world glTF exports essentially never emit them. */
static void mat4_decompose(const float m[16], float t[3], float q[4], float s[3]) {
    t[0] = m[12]; t[1] = m[13]; t[2] = m[14];

    float c0[3] = { m[0], m[1], m[2] };
    float c1[3] = { m[4], m[5], m[6] };
    float c2[3] = { m[8], m[9], m[10] };

    s[0] = sqrtf(c0[0] * c0[0] + c0[1] * c0[1] + c0[2] * c0[2]);
    s[1] = sqrtf(c1[0] * c1[0] + c1[1] * c1[1] + c1[2] * c1[2]);
    s[2] = sqrtf(c2[0] * c2[0] + c2[1] * c2[1] + c2[2] * c2[2]);

    const float eps = 1e-8f;
    if (s[0] > eps) { c0[0] /= s[0]; c0[1] /= s[0]; c0[2] /= s[0]; } else { c0[0] = 1; c0[1] = 0; c0[2] = 0; }
    if (s[1] > eps) { c1[0] /= s[1]; c1[1] /= s[1]; c1[2] /= s[1]; } else { c1[0] = 0; c1[1] = 1; c1[2] = 0; }
    if (s[2] > eps) { c2[0] /= s[2]; c2[1] /= s[2]; c2[2] /= s[2]; } else { c2[0] = 0; c2[1] = 0; c2[2] = 1; }

    /* Rij = row i, column j of the pure-rotation matrix whose columns are c0/c1/c2. */
    float r00 = c0[0], r10 = c0[1], r20 = c0[2];
    float r01 = c1[0], r11 = c1[1], r21 = c1[2];
    float r02 = c2[0], r12 = c2[1], r22 = c2[2];

    float trace = r00 + r11 + r22;
    if (trace > 0.0f) {
        float sc = sqrtf(trace + 1.0f) * 2.0f;
        q[3] = 0.25f * sc;
        q[0] = (r21 - r12) / sc;
        q[1] = (r02 - r20) / sc;
        q[2] = (r10 - r01) / sc;
    } else if (r00 > r11 && r00 > r22) {
        float sc = sqrtf(1.0f + r00 - r11 - r22) * 2.0f;
        q[3] = (r21 - r12) / sc;
        q[0] = 0.25f * sc;
        q[1] = (r01 + r10) / sc;
        q[2] = (r02 + r20) / sc;
    } else if (r11 > r22) {
        float sc = sqrtf(1.0f + r11 - r00 - r22) * 2.0f;
        q[3] = (r02 - r20) / sc;
        q[0] = (r01 + r10) / sc;
        q[1] = 0.25f * sc;
        q[2] = (r12 + r21) / sc;
    } else {
        float sc = sqrtf(1.0f + r22 - r00 - r11) * 2.0f;
        q[3] = (r10 - r01) / sc;
        q[0] = (r02 + r20) / sc;
        q[1] = (r12 + r21) / sc;
        q[2] = 0.25f * sc;
    }
}

int cgltf_wrap_load_hierarchy(const char* path, CgltfNodeHierarchy* out) {
    memset(out, 0, sizeof(*out));

    cgltf_options opts;
    memset(&opts, 0, sizeof(opts));
    cgltf_data* data = NULL;

    if (cgltf_parse_file(&opts, path, &data) != cgltf_result_success) return 1;
    /* No cgltf_load_buffers: transforms/names/hierarchy don't need accessor data. */

    if (data->nodes_count == 0) {
        cgltf_free(data);
        return 3;
    }

    CgltfNodeData* nodes = (CgltfNodeData*)calloc(data->nodes_count, sizeof(CgltfNodeData));
    if (!nodes) {
        cgltf_free(data);
        return 5;
    }

    cgltf_size node_count = data->nodes_count;
    for (cgltf_size ni = 0; ni < node_count; ni++) {
        cgltf_node* n = &data->nodes[ni];
        CgltfNodeData* o = &nodes[ni];

        copy_str(o->name, sizeof(o->name), n->name);
        o->parent_index = n->parent ? (int32_t)(n->parent - data->nodes) : -1;
        o->mesh_index = n->mesh ? (int32_t)(n->mesh - data->meshes) : -1;

        if (n->has_matrix) {
            mat4_decompose(n->matrix, o->translation, o->rotation, o->scale);
        } else {
            if (n->has_translation) memcpy(o->translation, n->translation, sizeof(o->translation));
            if (n->has_rotation) {
                memcpy(o->rotation, n->rotation, sizeof(o->rotation));
            } else {
                o->rotation[3] = 1.0f; /* identity quaternion xyzw */
            }
            if (n->has_scale) {
                memcpy(o->scale, n->scale, sizeof(o->scale));
            } else {
                o->scale[0] = o->scale[1] = o->scale[2] = 1.0f;
            }
        }
    }

    cgltf_free(data);

    out->nodes = nodes;
    out->node_count = (uint32_t)node_count;
    return 0;
}

void cgltf_wrap_free_hierarchy(CgltfNodeHierarchy* out) {
    if (!out || !out->nodes) return;
    free(out->nodes);
    out->nodes = NULL;
    out->node_count = 0;
}
