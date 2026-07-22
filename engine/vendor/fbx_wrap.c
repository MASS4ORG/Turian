/* Single translation unit: pulls in the vendored ufbx implementation, same
   pattern as cgltf_wrap.c's `CGLTF_IMPLEMENTATION` — one C source file to
   wire into build.zig instead of two. */
#include "ufbx.c"
#include "fbx_wrap.h"
#include <stdlib.h>
#include <string.h>
#include <math.h>

/* Normalize axes/units to the engine convention (right-handed, Y-up, meters,
   matching glTF) so no manual axis math is needed downstream, and bake the
   conversion straight into geometry rather than adding a root transform. */
static void fbx_wrap_opts(ufbx_load_opts* opts) {
    memset(opts, 0, sizeof(*opts));
    opts->target_axes = ufbx_axes_right_handed_y_up;
    opts->target_unit_meters = 1.0f;
    opts->space_conversion = UFBX_SPACE_CONVERSION_MODIFY_GEOMETRY;
    opts->generate_missing_normals = true;
    opts->ignore_missing_external_files = true;
}

static void copy_str(char* dst, size_t cap, const char* src, size_t len) {
    if (cap == 0) return;
    if (!src) { dst[0] = 0; return; }
    if (len >= cap) len = cap - 1;
    memcpy(dst, src, len);
    dst[len] = 0;
}

/* ── Geometry ─────────────────────────────────────────────────────────────── */

typedef struct {
    FbxMeshData* data;
    uint32_t count;
    uint32_t cap;
} ChunkArray;

static int chunk_reserve(ChunkArray* arr) {
    if (arr->count < arr->cap) return 1;
    uint32_t new_cap = arr->cap ? arr->cap * 2 : 16;
    FbxMeshData* p = (FbxMeshData*)realloc(arr->data, (size_t)new_cap * sizeof(FbxMeshData));
    if (!p) return 0;
    arr->data = p;
    arr->cap = new_cap;
    return 1;
}

static void free_chunk(FbxMeshData* c) {
    free(c->positions);
    free(c->normals);
    free(c->uvs);
    free(c->indices);
}

/* ufbx emits fully de-indexed geometry (one vertex per triangle corner); weld
   matching (pos, normal, uv) corners back into a real index buffer via ufbx's
   own hashing pass. Rewrites `c->indices` and shrinks `c->vertex_count` in
   place; leaves the chunk untouched if welding fails for any reason (still a
   valid, merely unwelded, chunk). */
static void weld_chunk(FbxMeshData* c) {
    uint32_t* welded = (uint32_t*)malloc((size_t)c->index_count * sizeof(uint32_t));
    if (!welded) return;

    ufbx_vertex_stream streams[3];
    size_t num_streams = 0;
    streams[num_streams++] = (ufbx_vertex_stream){ c->positions, c->vertex_count, 3 * sizeof(float) };
    if (c->normals) streams[num_streams++] = (ufbx_vertex_stream){ c->normals, c->vertex_count, 3 * sizeof(float) };
    if (c->uvs) streams[num_streams++] = (ufbx_vertex_stream){ c->uvs, c->vertex_count, 2 * sizeof(float) };

    ufbx_error error;
    size_t unique = ufbx_generate_indices(streams, num_streams, welded, c->index_count, NULL, &error);
    if (unique == 0 || unique > c->vertex_count) {
        free(welded);
        return;
    }

    free(c->indices);
    c->indices = welded;
    c->vertex_count = (uint32_t)unique;
}

int fbx_wrap_load_all(const char* path, FbxMultiMeshData* out) {
    memset(out, 0, sizeof(*out));

    ufbx_load_opts opts;
    fbx_wrap_opts(&opts);
    ufbx_error error;
    ufbx_scene* scene = ufbx_load_file(path, &opts, &error);
    if (!scene) return 1;

    ChunkArray chunks;
    memset(&chunks, 0, sizeof(chunks));
    uint32_t tri_cap = 0;
    uint32_t* tri_buf = NULL;

    for (size_t ni = 0; ni < scene->nodes.count; ni++) {
        ufbx_node* node = scene->nodes.data[ni];
        ufbx_mesh* mesh = node->mesh;
        if (!mesh || mesh->num_indices == 0) continue;

        if ((uint32_t)mesh->max_face_triangles > tri_cap) {
            uint32_t need = (uint32_t)mesh->max_face_triangles;
            uint32_t* p = (uint32_t*)realloc(tri_buf, (size_t)need * 3 * sizeof(uint32_t));
            if (!p) continue;
            tri_buf = p;
            tri_cap = need;
        }

        ufbx_matrix normal_mat = ufbx_matrix_for_normals(&node->geometry_to_world);

        for (size_t pi = 0; pi < mesh->material_parts.count; pi++) {
            ufbx_mesh_part* part = &mesh->material_parts.data[pi];
            if (part->num_triangles == 0) continue;

            uint32_t vcount = (uint32_t)(part->num_triangles * 3);

            float* positions = (float*)malloc((size_t)vcount * 3 * sizeof(float));
            float* normals = mesh->vertex_normal.exists ? (float*)malloc((size_t)vcount * 3 * sizeof(float)) : NULL;
            float* uvs = mesh->vertex_uv.exists ? (float*)malloc((size_t)vcount * 2 * sizeof(float)) : NULL;
            uint32_t* indices = (uint32_t*)malloc((size_t)vcount * sizeof(uint32_t));
            if (!positions || !indices) {
                free(positions); free(normals); free(uvs); free(indices);
                continue;
            }

            uint32_t vi = 0;
            for (size_t fj = 0; fj < part->face_indices.count && vi < vcount; fj++) {
                uint32_t face_idx = part->face_indices.data[fj];
                ufbx_face face = mesh->faces.data[face_idx];
                uint32_t ntri = ufbx_triangulate_face(tri_buf, (size_t)tri_cap * 3, mesh, face);
                if (vi + ntri * 3 > vcount) ntri = (vcount - vi) / 3;

                for (uint32_t t = 0; t < ntri; t++) {
                    for (uint32_t k = 0; k < 3; k++) {
                        uint32_t ci = tri_buf[t * 3 + k];

                        ufbx_vec3 p = ufbx_get_vertex_vec3(&mesh->vertex_position, ci);
                        p = ufbx_transform_position(&node->geometry_to_world, p);
                        positions[vi * 3 + 0] = (float)p.x;
                        positions[vi * 3 + 1] = (float)p.y;
                        positions[vi * 3 + 2] = (float)p.z;

                        if (normals) {
                            ufbx_vec3 n = ufbx_get_vertex_vec3(&mesh->vertex_normal, ci);
                            n = ufbx_transform_direction(&normal_mat, n);
                            double len = sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                            if (len > 1e-9) {
                                n.x = (ufbx_real)(n.x / len);
                                n.y = (ufbx_real)(n.y / len);
                                n.z = (ufbx_real)(n.z / len);
                            }
                            normals[vi * 3 + 0] = (float)n.x;
                            normals[vi * 3 + 1] = (float)n.y;
                            normals[vi * 3 + 2] = (float)n.z;
                        }

                        if (uvs) {
                            ufbx_vec2 uv = ufbx_get_vertex_vec2(&mesh->vertex_uv, ci);
                            uvs[vi * 2 + 0] = (float)uv.x;
                            uvs[vi * 2 + 1] = (float)uv.y;
                        }

                        indices[vi] = vi;
                        vi++;
                    }
                }
            }

            if (vi == 0 || !chunk_reserve(&chunks)) {
                free(positions); free(normals); free(uvs); free(indices);
                continue;
            }

            ufbx_material* mat = NULL;
            if (part->index < node->materials.count) mat = node->materials.data[part->index];
            else if (part->index < mesh->materials.count) mat = mesh->materials.data[part->index];

            FbxMeshData* c = &chunks.data[chunks.count++];
            c->positions = positions;
            c->normals = normals;
            c->uvs = uvs;
            c->indices = indices;
            c->vertex_count = vi;
            c->index_count = vi;
            c->has_normals = normals ? 1 : 0;
            c->has_uvs = uvs ? 1 : 0;
            c->material_index = mat ? (int)mat->typed_id : -1;

            weld_chunk(c);
        }
    }

    free(tri_buf);
    ufbx_free_scene(scene);

    if (chunks.count == 0) {
        free(chunks.data);
        return 2;
    }

    out->primitives = chunks.data;
    out->primitive_count = chunks.count;
    return 0;
}

void fbx_wrap_free_all(FbxMultiMeshData* out) {
    if (!out || !out->primitives) return;
    for (uint32_t i = 0; i < out->primitive_count; i++)
        free_chunk(&out->primitives[i]);
    free(out->primitives);
    out->primitives = NULL;
    out->primitive_count = 0;
}

/* ── Node hierarchy ───────────────────────────────────────────────────────── */

typedef struct {
    FbxMeshLocalData* data;
    uint32_t count;
    uint32_t cap;
} LocalChunkArray;

static int local_chunk_reserve(LocalChunkArray* arr) {
    if (arr->count < arr->cap) return 1;
    uint32_t new_cap = arr->cap ? arr->cap * 2 : 16;
    FbxMeshLocalData* p = (FbxMeshLocalData*)realloc(arr->data, (size_t)new_cap * sizeof(FbxMeshLocalData));
    if (!p) return 0;
    arr->data = p;
    arr->cap = new_cap;
    return 1;
}

static void free_local_chunk(FbxMeshLocalData* c) {
    free(c->positions);
    free(c->normals);
    free(c->uvs);
    free(c->indices);
}

/* weld_chunk operates on FbxMeshData; FbxMeshLocalData has the identical
   vertex/index layout (only the trailing mesh_index differs), so reuse it via
   a same-layout cast rather than duplicating the welding pass. */
static void weld_local_chunk(FbxMeshLocalData* c) {
    weld_chunk((FbxMeshData*)c);
}

int fbx_wrap_load_meshes(const char* path, FbxMultiMeshLocalData* out) {
    memset(out, 0, sizeof(*out));

    ufbx_load_opts opts;
    fbx_wrap_opts(&opts);
    ufbx_error error;
    ufbx_scene* scene = ufbx_load_file(path, &opts, &error);
    if (!scene) return 1;

    LocalChunkArray chunks;
    memset(&chunks, 0, sizeof(chunks));
    uint32_t tri_cap = 0;
    uint32_t* tri_buf = NULL;

    uint32_t mesh_count = (uint32_t)scene->meshes.count;
    FbxMeshName* mesh_names = mesh_count ? (FbxMeshName*)calloc(mesh_count, sizeof(FbxMeshName)) : NULL;
    if (mesh_count && !mesh_names) {
        ufbx_free_scene(scene);
        return 5;
    }
    for (uint32_t mi = 0; mi < mesh_count; mi++) {
        ufbx_mesh* mesh = scene->meshes.data[mi];
        copy_str(mesh_names[mi].name, sizeof(mesh_names[mi].name), mesh->name.data, mesh->name.length);
    }

    for (size_t mi = 0; mi < scene->meshes.count; mi++) {
        ufbx_mesh* mesh = scene->meshes.data[mi];
        if (mesh->num_indices == 0) continue;

        /* The mesh's own geometric offset (FBX's separate, non-inherited
           "geometric transform"), taken from its first instance -- shared by
           construction across every node referencing this mesh in practice
           (ufbx models it per-node, but authoring tools never vary it per
           instance). Never the node's placement in the scene. */
        ufbx_matrix geo_mat;
        if (mesh->instances.count > 0) {
            geo_mat = mesh->instances.data[0]->geometry_to_node;
        } else {
            geo_mat = ufbx_identity_matrix;
        }
        ufbx_matrix normal_mat = ufbx_matrix_for_normals(&geo_mat);

        if ((uint32_t)mesh->max_face_triangles > tri_cap) {
            uint32_t need = (uint32_t)mesh->max_face_triangles;
            uint32_t* p = (uint32_t*)realloc(tri_buf, (size_t)need * 3 * sizeof(uint32_t));
            if (!p) continue;
            tri_buf = p;
            tri_cap = need;
        }

        for (size_t pi = 0; pi < mesh->material_parts.count; pi++) {
            ufbx_mesh_part* part = &mesh->material_parts.data[pi];
            if (part->num_triangles == 0) continue;

            uint32_t vcount = (uint32_t)(part->num_triangles * 3);

            float* positions = (float*)malloc((size_t)vcount * 3 * sizeof(float));
            float* normals = mesh->vertex_normal.exists ? (float*)malloc((size_t)vcount * 3 * sizeof(float)) : NULL;
            float* uvs = mesh->vertex_uv.exists ? (float*)malloc((size_t)vcount * 2 * sizeof(float)) : NULL;
            uint32_t* indices = (uint32_t*)malloc((size_t)vcount * sizeof(uint32_t));
            if (!positions || !indices) {
                free(positions); free(normals); free(uvs); free(indices);
                continue;
            }

            uint32_t vi = 0;
            for (size_t fj = 0; fj < part->face_indices.count && vi < vcount; fj++) {
                uint32_t face_idx = part->face_indices.data[fj];
                ufbx_face face = mesh->faces.data[face_idx];
                uint32_t ntri = ufbx_triangulate_face(tri_buf, (size_t)tri_cap * 3, mesh, face);
                if (vi + ntri * 3 > vcount) ntri = (vcount - vi) / 3;

                for (uint32_t t = 0; t < ntri; t++) {
                    for (uint32_t k = 0; k < 3; k++) {
                        uint32_t ci = tri_buf[t * 3 + k];

                        ufbx_vec3 p = ufbx_get_vertex_vec3(&mesh->vertex_position, ci);
                        p = ufbx_transform_position(&geo_mat, p);
                        positions[vi * 3 + 0] = (float)p.x;
                        positions[vi * 3 + 1] = (float)p.y;
                        positions[vi * 3 + 2] = (float)p.z;

                        if (normals) {
                            ufbx_vec3 n = ufbx_get_vertex_vec3(&mesh->vertex_normal, ci);
                            n = ufbx_transform_direction(&normal_mat, n);
                            double len = sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                            if (len > 1e-9) {
                                n.x = (ufbx_real)(n.x / len);
                                n.y = (ufbx_real)(n.y / len);
                                n.z = (ufbx_real)(n.z / len);
                            }
                            normals[vi * 3 + 0] = (float)n.x;
                            normals[vi * 3 + 1] = (float)n.y;
                            normals[vi * 3 + 2] = (float)n.z;
                        }

                        if (uvs) {
                            ufbx_vec2 uv = ufbx_get_vertex_vec2(&mesh->vertex_uv, ci);
                            uvs[vi * 2 + 0] = (float)uv.x;
                            uvs[vi * 2 + 1] = (float)uv.y;
                        }

                        indices[vi] = vi;
                        vi++;
                    }
                }
            }

            if (vi == 0 || !local_chunk_reserve(&chunks)) {
                free(positions); free(normals); free(uvs); free(indices);
                continue;
            }

            /* Mesh's own default material assignment (ufbx_mesh.materials),
               not any per-node override -- see FbxMultiMeshLocalData's doc
               comment in fbx_wrap.h. */
            ufbx_material* mat = part->index < mesh->materials.count ? mesh->materials.data[part->index] : NULL;

            FbxMeshLocalData* c = &chunks.data[chunks.count++];
            c->positions = positions;
            c->normals = normals;
            c->uvs = uvs;
            c->indices = indices;
            c->vertex_count = vi;
            c->index_count = vi;
            c->has_normals = normals ? 1 : 0;
            c->has_uvs = uvs ? 1 : 0;
            c->material_index = mat ? (int)mat->typed_id : -1;
            c->mesh_index = (int)mi;

            weld_local_chunk(c);
        }
    }

    free(tri_buf);
    ufbx_free_scene(scene);

    if (chunks.count == 0) {
        free(chunks.data);
        free(mesh_names);
        return 2;
    }

    out->primitives = chunks.data;
    out->primitive_count = chunks.count;
    out->mesh_names = mesh_names;
    out->mesh_count = mesh_count;
    return 0;
}

void fbx_wrap_free_meshes(FbxMultiMeshLocalData* out) {
    if (!out) return;
    if (out->primitives) {
        for (uint32_t i = 0; i < out->primitive_count; i++)
            free_local_chunk(&out->primitives[i]);
        free(out->primitives);
    }
    free(out->mesh_names);
    out->primitives = NULL;
    out->primitive_count = 0;
    out->mesh_names = NULL;
    out->mesh_count = 0;
}

int fbx_wrap_load_hierarchy(const char* path, FbxNodeHierarchy* out) {
    memset(out, 0, sizeof(*out));

    ufbx_load_opts opts;
    fbx_wrap_opts(&opts);
    ufbx_error error;
    ufbx_scene* scene = ufbx_load_file(path, &opts, &error);
    if (!scene) return 1;

    if (scene->nodes.count == 0) {
        ufbx_free_scene(scene);
        return 3;
    }

    FbxNodeData* nodes = (FbxNodeData*)calloc(scene->nodes.count, sizeof(FbxNodeData));
    if (!nodes) {
        ufbx_free_scene(scene);
        return 5;
    }

    for (size_t ni = 0; ni < scene->nodes.count; ni++) {
        ufbx_node* node = scene->nodes.data[ni];
        FbxNodeData* o = &nodes[ni];

        copy_str(o->name, sizeof(o->name), node->name.data, node->name.length);
        o->parent_index = node->parent ? (int32_t)node->parent->typed_id : -1;
        o->mesh_index = node->mesh ? (int32_t)node->mesh->typed_id : -1;

        o->translation[0] = (float)node->local_transform.translation.x;
        o->translation[1] = (float)node->local_transform.translation.y;
        o->translation[2] = (float)node->local_transform.translation.z;
        o->rotation[0] = (float)node->local_transform.rotation.x;
        o->rotation[1] = (float)node->local_transform.rotation.y;
        o->rotation[2] = (float)node->local_transform.rotation.z;
        o->rotation[3] = (float)node->local_transform.rotation.w;
        o->scale[0] = (float)node->local_transform.scale.x;
        o->scale[1] = (float)node->local_transform.scale.y;
        o->scale[2] = (float)node->local_transform.scale.z;
    }

    uint32_t node_count = (uint32_t)scene->nodes.count;
    ufbx_free_scene(scene);

    out->nodes = nodes;
    out->node_count = node_count;
    return 0;
}

void fbx_wrap_free_hierarchy(FbxNodeHierarchy* out) {
    if (!out || !out->nodes) return;
    free(out->nodes);
    out->nodes = NULL;
    out->node_count = 0;
}

/* ── Materials & images ──────────────────────────────────────────────────── */

static int str_ends_with_ci(const char* data, size_t len, const char* suffix_lower) {
    size_t slen = strlen(suffix_lower);
    if (len < slen) return 0;
    const char* start = data + (len - slen);
    for (size_t i = 0; i < slen; i++) {
        char a = start[i];
        if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
        if (a != suffix_lower[i]) return 0;
    }
    return 1;
}

static const char* mime_for_name(const char* data, size_t len) {
    if (!data) return "";
    if (str_ends_with_ci(data, len, ".png")) return "image/png";
    if (str_ends_with_ci(data, len, ".jpg")) return "image/jpeg";
    if (str_ends_with_ci(data, len, ".jpeg")) return "image/jpeg";
    if (str_ends_with_ci(data, len, ".tga")) return "image/tga";
    if (str_ends_with_ci(data, len, ".bmp")) return "image/bmp";
    if (str_ends_with_ci(data, len, ".dds")) return "image/dds";
    return "";
}

static FbxTexRef texref_from_map(const ufbx_material_map* map) {
    FbxTexRef r;
    r.has_texture = 0;
    r.image_index = -1;
    r.uv_set = 0;
    if (map->texture) {
        r.has_texture = 1;
        r.image_index = (int)map->texture->typed_id;
    }
    return r;
}

/* ufbx normalizes every shading model (Lambert/Phong/StingrayPBS/PBR/glTF-in-
   FBX) into `material->pbr`, so this one mapping covers them all. FBX has no
   single packed metallic-roughness texture like glTF, so that slot (and
   occlusion, which FBX's pbr maps don't expose) only ever carries a scalar
   factor -- never a texture binding. */
static void fill_material(FbxMaterial* o, const ufbx_material* m) {
    memset(o, 0, sizeof(*o));
    copy_str(o->name, sizeof(o->name), m->name.data, m->name.length);

    o->base_color[0] = o->base_color[1] = o->base_color[2] = o->base_color[3] = 1.0f;
    /* Classic (non-PBR) shading models -- Phong/Lambert, the overwhelming
       majority of real-world FBX content -- have no metalness concept at all,
       so ufbx's pbr.metalness never carries a value for them and this default
       is what every such material actually renders with. 1.0 (glTF's raw
       spec default, intended for an authored PBR workflow) makes every
       surface a pure mirror: F0 becomes the albedo texture and the diffuse
       term vanishes entirely, so converted classic materials show only a
       tinted specular reflection and look completely textureless. 0.0
       (dielectric) is the correct assumption for content that never declared
       a metalness value in the first place. */
    o->metallic = 0.0f;
    o->roughness = 1.0f;
    o->normal_scale = 1.0f;
    o->occlusion_strength = 1.0f;
    o->emissive_strength = 1.0f;
    o->alpha_cutoff = 0.5f;

    const ufbx_material_pbr_maps* pbr = &m->pbr;

    if (pbr->base_color.has_value) {
        o->base_color[0] = (float)pbr->base_color.value_vec4.x;
        o->base_color[1] = (float)pbr->base_color.value_vec4.y;
        o->base_color[2] = (float)pbr->base_color.value_vec4.z;
    }
    if (pbr->opacity.has_value) o->base_color[3] = (float)pbr->opacity.value_real;
    o->albedo = texref_from_map(&pbr->base_color);

    if (pbr->metalness.has_value) o->metallic = (float)pbr->metalness.value_real;
    if (pbr->roughness.has_value) o->roughness = (float)pbr->roughness.value_real;

    if (pbr->emission_color.has_value) {
        o->emissive[0] = (float)pbr->emission_color.value_vec3.x;
        o->emissive[1] = (float)pbr->emission_color.value_vec3.y;
        o->emissive[2] = (float)pbr->emission_color.value_vec3.z;
    }
    if (pbr->emission_factor.has_value) o->emissive_strength = (float)pbr->emission_factor.value_real;
    o->emissive_tex = texref_from_map(&pbr->emission_color);

    o->normal = texref_from_map(&pbr->normal_map);

    int opacity_textured = pbr->opacity.texture != NULL;
    int opacity_transparent = pbr->opacity.has_value && pbr->opacity.value_real < 0.999;
    o->alpha_mode = (opacity_textured || opacity_transparent) ? FBX_WRAP_ALPHA_BLEND : FBX_WRAP_ALPHA_OPAQUE;
    o->double_sided = m->features.double_sided.enabled ? 1 : 0;
}

static void fill_image(FbxImage* o, const ufbx_texture* t) {
    memset(o, 0, sizeof(*o));
    copy_str(o->name, sizeof(o->name), t->name.data, t->name.length);

    ufbx_string fn = t->relative_filename.length ? t->relative_filename : t->filename;

    if (t->content.data && t->content.size > 0) {
        o->data = (const unsigned char*)t->content.data;
        o->data_size = (uint32_t)t->content.size;
        const char* mime = mime_for_name(fn.data, fn.length);
        copy_str(o->mime_type, sizeof(o->mime_type), mime, strlen(mime));
    } else if (fn.length > 0) {
        copy_str(o->uri, sizeof(o->uri), fn.data, fn.length);
    }
}

int fbx_wrap_load_model(const char* path, FbxModelData* out) {
    memset(out, 0, sizeof(*out));

    ufbx_load_opts opts;
    fbx_wrap_opts(&opts);
    ufbx_error error;
    ufbx_scene* scene = ufbx_load_file(path, &opts, &error);
    if (!scene) return 1;

    uint32_t mat_count = (uint32_t)scene->materials.count;
    uint32_t img_count = (uint32_t)scene->textures.count;

    FbxMaterial* mats = mat_count ? (FbxMaterial*)calloc(mat_count, sizeof(FbxMaterial)) : NULL;
    FbxImage* imgs = img_count ? (FbxImage*)calloc(img_count, sizeof(FbxImage)) : NULL;
    if ((mat_count && !mats) || (img_count && !imgs)) {
        free(mats); free(imgs);
        ufbx_free_scene(scene);
        return 5;
    }

    for (uint32_t i = 0; i < mat_count; i++) fill_material(&mats[i], scene->materials.data[i]);
    for (uint32_t i = 0; i < img_count; i++) fill_image(&imgs[i], scene->textures.data[i]);

    out->materials = mats;
    out->material_count = mat_count;
    out->images = imgs;
    out->image_count = img_count;
    out->_scene = scene;
    return 0;
}

void fbx_wrap_free_model(FbxModelData* out) {
    if (!out) return;
    free(out->materials);
    free(out->images);
    if (out->_scene) ufbx_free_scene((ufbx_scene*)out->_scene);
    out->materials = NULL;
    out->images = NULL;
    out->_scene = NULL;
    out->material_count = 0;
    out->image_count = 0;
}
