// traffic_tar_builder.cpp
//
// Builds a Valhalla-compatible traffic.tar from:
//   1. A live traffic CSV (way_id,speed_kmh) — from the OpenTrafficDB API endpoint
//   2. A way_edges.txt mapping file — from valhalla_ways_to_edges
//   3. A Valhalla tile extract (.tar with .gph tiles) — from valhalla_build_extract
//
// The output traffic.tar matches the binary layout Valhalla expects:
//   - index.bin (first entry): array of {offset: u64, tile_id: u32, size: u32}
//   - Per .gph tile: TrafficTileHeader (32 bytes) + N × TrafficSpeed (8 bytes each)
//
// Build:
//   g++ -std=c++17 -O2 -o traffic_tar_builder traffic_tar_builder.cpp
//
// Usage:
//   # 1. Generate the way→edge mapping (one-time, redo after tile rebuild):
//   valhalla_ways_to_edges --config valhalla.json way_edges.txt
//
//   # 2. Fetch live speeds from OpenTrafficDB:
//   curl -H "Authorization: Bearer $TOKEN" https://your-server/api/v1/traffic/live/csv -o live.csv
//
//   # 3. Build traffic.tar:
//   ./traffic_tar_builder --tiles valhalla_tiles.tar --csv live.csv --way-edges way_edges.txt -o traffic.tar

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

// ── Valhalla GraphId decomposition ──────────────────────────────────────────
//
// A Valhalla GraphId is a uint64 packed as:
//   bits  0..2  : level     (3 bits)
//   bits  3..24 : tile_id   (22 bits)
//   bits 25..45 : id/offset (21 bits)
//
// tile_path_to_id packs level|tile_id into a single uint32 (the "tile key")
// that matches what goes into TrafficTileHeader.tile_id and index.bin.

static constexpr uint64_t GRAPHID_LEVEL_BITS   = 3;
static constexpr uint64_t GRAPHID_TILE_BITS    = 22;
static constexpr uint64_t GRAPHID_ID_BITS      = 21;
static constexpr uint64_t GRAPHID_LEVEL_MASK   = (1ULL << GRAPHID_LEVEL_BITS) - 1;
static constexpr uint64_t GRAPHID_TILE_MASK    = (1ULL << GRAPHID_TILE_BITS) - 1;
static constexpr uint64_t GRAPHID_ID_MASK      = (1ULL << GRAPHID_ID_BITS) - 1;

struct GraphId {
    uint32_t level;
    uint32_t tile_id;
    uint32_t edge_id;   // directed edge offset within the tile

    // The tile key matches TrafficTileHeader.tile_id / index.bin tile_id
    uint32_t tile_key() const { return level | (tile_id << GRAPHID_LEVEL_BITS); }
};

static GraphId decode_graph_id(uint64_t raw) {
    return {
        static_cast<uint32_t>(raw & GRAPHID_LEVEL_MASK),
        static_cast<uint32_t>((raw >> GRAPHID_LEVEL_BITS) & GRAPHID_TILE_MASK),
        static_cast<uint32_t>((raw >> (GRAPHID_LEVEL_BITS + GRAPHID_TILE_BITS)) & GRAPHID_ID_MASK)
    };
}

// ── Valhalla binary format structures ────────────────────────────────────────

static constexpr uint32_t TRAFFIC_TILE_VERSION = 3;
static constexpr uint32_t UNKNOWN_TRAFFIC_SPEED_RAW = (1 << 7) - 1; // 127
static constexpr uint32_t UNKNOWN_CONGESTION_VAL = 0;
static constexpr uint32_t MAX_CONGESTION_VAL = 63;

#pragma pack(push, 1)
struct TrafficTileHeader {
    uint64_t tile_id;
    uint64_t last_update;         // seconds since epoch
    uint32_t directed_edge_count;
    uint32_t traffic_tile_version;
    uint32_t spare2;
    uint32_t spare3;
};
static_assert(sizeof(TrafficTileHeader) == 32, "TrafficTileHeader must be 32 bytes");

struct TrafficSpeed {
    uint64_t overall_encoded_speed : 7;
    uint64_t encoded_speed1        : 7;
    uint64_t encoded_speed2        : 7;
    uint64_t encoded_speed3        : 7;
    uint64_t breakpoint1           : 8;
    uint64_t breakpoint2           : 8;
    uint64_t congestion1           : 6;
    uint64_t congestion2           : 6;
    uint64_t congestion3           : 6;
    uint64_t has_incidents         : 1;
    uint64_t spare                 : 1;
};
static_assert(sizeof(TrafficSpeed) == 8, "TrafficSpeed must be 8 bytes");
#pragma pack(pop)

// ── Tar format ───────────────────────────────────────────────────────────────

static constexpr size_t TAR_BLOCK = 512;

struct TarHeader {
    char name[100];
    char mode[8];
    char uid[8];
    char gid[8];
    char size[12];
    char mtime[12];
    char checksum[8];
    char typeflag;
    char linkname[100];
    char magic[6];
    char version[2];
    char uname[32];
    char gname[32];
    char devmajor[8];
    char devminor[8];
    char prefix[155];
    char padding[12];
};
static_assert(sizeof(TarHeader) == 512, "TarHeader must be 512 bytes");

// ── Graph tile header parsing ────────────────────────────────────────────────
//
// The directed edge count lives in a bitfield at bytes 40-48 of the .gph header.
// Layout: nodecount(21) | directededgecount(21) | predictedspeeds_count(21) | spare(1)

static constexpr size_t GRAPHTILE_SKIP_BYTES = 40;

struct GraphTileBitfield {
    uint64_t nodecount             : 21;
    uint64_t directededgecount     : 21;
    uint64_t predictedspeeds_count : 21;
    uint64_t spare1                : 1;
};
static_assert(sizeof(GraphTileBitfield) == 8, "GraphTileBitfield must be 8 bytes");

// ── Index.bin entry (matches valhalla_build_extract format) ──────────────────

struct IndexEntry {
    uint64_t offset;
    uint32_t tile_id;
    uint32_t size;
};

// ── Tile path ↔ tile key conversion ─────────────────────────────────────────

static uint32_t tile_path_to_key(const std::string& path) {
    // path like "2/000/456/789.gph" → level=2, idx=456789 → key = level | (idx << 3)
    auto dot = path.rfind('.');
    std::string no_ext = (dot != std::string::npos) ? path.substr(0, dot) : path;

    auto slash = no_ext.find('/');
    if (slash == std::string::npos) {
        std::cerr << "Invalid tile path: " << path << "\n";
        std::exit(1);
    }

    uint32_t level = std::stoul(no_ext.substr(0, slash));
    std::string idx_str;
    for (size_t i = slash + 1; i < no_ext.size(); i++) {
        if (no_ext[i] != '/') idx_str += no_ext[i];
    }
    uint32_t idx = std::stoul(idx_str);

    return level | (idx << GRAPHID_LEVEL_BITS);
}

// ── Tar helpers ──────────────────────────────────────────────────────────────

static void write_octal(char* dst, size_t len, uint64_t val) {
    std::snprintf(dst, len, "%0*llo", static_cast<int>(len - 1),
                  static_cast<unsigned long long>(val));
}

static void compute_checksum(TarHeader& hdr) {
    std::memset(hdr.checksum, ' ', 8);
    uint32_t sum = 0;
    auto* bytes = reinterpret_cast<const uint8_t*>(&hdr);
    for (size_t i = 0; i < sizeof(TarHeader); i++)
        sum += bytes[i];
    std::snprintf(hdr.checksum, 7, "%06o", sum);
    hdr.checksum[6] = '\0';
    hdr.checksum[7] = ' ';
}

static TarHeader make_tar_header(const std::string& name, size_t size) {
    TarHeader hdr{};
    std::strncpy(hdr.name, name.c_str(), sizeof(hdr.name) - 1);
    write_octal(hdr.mode,  sizeof(hdr.mode),  0644);
    write_octal(hdr.uid,   sizeof(hdr.uid),   0);
    write_octal(hdr.gid,   sizeof(hdr.gid),   0);
    write_octal(hdr.size,  sizeof(hdr.size),   size);
    write_octal(hdr.mtime, sizeof(hdr.mtime), static_cast<uint64_t>(std::time(nullptr)));
    hdr.typeflag = '0';
    std::memcpy(hdr.magic, "ustar", 5);
    hdr.magic[5] = '\0';
    hdr.version[0] = '0';
    hdr.version[1] = '0';
    compute_checksum(hdr);
    return hdr;
}

static size_t pad_to_block(size_t size) {
    auto rem = size % TAR_BLOCK;
    return rem ? TAR_BLOCK - rem : 0;
}

// ── Tar scanner ──────────────────────────────────────────────────────────────

struct TarMember {
    std::string name;
    size_t      data_offset;
    size_t      size;
};

static std::vector<TarMember> scan_tar(const std::string& tar_path) {
    std::ifstream f(tar_path, std::ios::binary);
    if (!f) {
        std::cerr << "Cannot open tile tar: " << tar_path << "\n";
        std::exit(1);
    }

    std::vector<TarMember> members;
    TarHeader hdr{};

    while (f.read(reinterpret_cast<char*>(&hdr), sizeof(hdr))) {
        if (hdr.name[0] == '\0') break;

        std::string name(hdr.name);
        while (!name.empty() && name.back() == '\0') name.pop_back();

        size_t size = std::strtoull(hdr.size, nullptr, 8);
        size_t data_offset = static_cast<size_t>(f.tellg());

        if (name.size() > 4 && name.substr(name.size() - 4) == ".gph")
            members.push_back({name, data_offset, size});

        f.seekg(static_cast<std::streamoff>(size + pad_to_block(size)), std::ios::cur);
    }
    return members;
}

// ── Parse live traffic CSV ───────────────────────────────────────────────────

struct LiveSpeed {
    float speed_kmh;
    float freeflow_kmh;
};

static std::unordered_map<int64_t, LiveSpeed> parse_csv(const std::string& csv_path) {
    std::ifstream f(csv_path);
    if (!f) {
        std::cerr << "Cannot open CSV: " << csv_path << "\n";
        std::exit(1);
    }

    std::unordered_map<int64_t, LiveSpeed> speeds;
    std::string line;
    std::getline(f, line); // skip header

    while (std::getline(f, line)) {
        if (line.empty()) continue;
        // Format: way_id,speed_kmh,freeflow_kmh
        auto c1 = line.find(',');
        if (c1 == std::string::npos) continue;
        auto c2 = line.find(',', c1 + 1);
        if (c2 == std::string::npos) continue;

        int64_t way_id      = std::stoll(line.substr(0, c1));
        float speed_kmh     = std::stof(line.substr(c1 + 1, c2 - c1 - 1));
        float freeflow_kmh  = std::stof(line.substr(c2 + 1));
        speeds[way_id] = {speed_kmh, freeflow_kmh};
    }

    std::cerr << "Loaded " << speeds.size() << " live speeds from CSV\n";
    return speeds;
}

// ── Parse way_edges.txt (from valhalla_ways_to_edges) ────────────────────────
//
// Format per line: osm_way_id,direction,graph_id[,direction,graph_id]*
// Example:         984719585,1,110964530744,1,112508034616,1,112843578936
//
// We build a map: tile_key → [(edge_offset, osm_way_id), ...] so that when
// writing each traffic tile we can look up which edges have live data.

struct EdgeMapping {
    uint32_t edge_offset;
    int64_t  osm_way_id;
};

static std::unordered_map<uint32_t, std::vector<EdgeMapping>>
parse_way_edges(const std::string& path) {
    std::ifstream f(path);
    if (!f) {
        std::cerr << "Cannot open way_edges file: " << path << "\n";
        std::exit(1);
    }

    std::unordered_map<uint32_t, std::vector<EdgeMapping>> tile_edges;
    std::string line;
    size_t total_mappings = 0;

    while (std::getline(f, line)) {
        if (line.empty()) continue;

        // Parse: way_id,dir,gid[,dir,gid]*
        std::istringstream ss(line);
        std::string token;

        if (!std::getline(ss, token, ',')) continue;
        int64_t way_id = std::stoll(token);

        while (std::getline(ss, token, ',')) {
            // direction (skip — we apply the same speed to both directions)
            // int direction = std::stoi(token);

            if (!std::getline(ss, token, ',')) break;
            uint64_t raw_graph_id = std::stoull(token);

            auto gid = decode_graph_id(raw_graph_id);
            uint32_t key = gid.tile_key();
            tile_edges[key].push_back({gid.edge_id, way_id});
            total_mappings++;
        }
    }

    std::cerr << "Loaded " << total_mappings << " edge mappings across "
              << tile_edges.size() << " tiles\n";
    return tile_edges;
}

// ── Encode a speed into a TrafficSpeed struct ────────────────────────────────

static constexpr float MAX_TRAFFIC_KPH =
    static_cast<float>((UNKNOWN_TRAFFIC_SPEED_RAW - 1) << 1); // 252

// Congestion is 6 bits: 0 = unknown, 1 = free flow, 63 = max congestion.
// Derived from speed / freeflow ratio.
static uint32_t compute_congestion(float speed_kmh, float freeflow_kmh) {
    if (freeflow_kmh <= 0.0f) return UNKNOWN_CONGESTION_VAL;

    float ratio = speed_kmh / freeflow_kmh;
    // ratio >= 1.0 → free flow (1), ratio → 0.0 → max congestion (63)
    float congestion = 1.0f + (1.0f - std::min(ratio, 1.0f)) * 62.0f;
    uint32_t val = static_cast<uint32_t>(std::round(congestion));
    if (val < 1) val = 1;
    if (val > MAX_CONGESTION_VAL) val = MAX_CONGESTION_VAL;
    return val;
}

static TrafficSpeed encode_speed(float speed_kmh, float freeflow_kmh) {
    TrafficSpeed ts{};

    if (speed_kmh <= 0.0f || std::isnan(speed_kmh)) {
        ts.overall_encoded_speed = UNKNOWN_TRAFFIC_SPEED_RAW;
        ts.encoded_speed1 = UNKNOWN_TRAFFIC_SPEED_RAW;
        ts.encoded_speed2 = UNKNOWN_TRAFFIC_SPEED_RAW;
        ts.encoded_speed3 = UNKNOWN_TRAFFIC_SPEED_RAW;
        ts.breakpoint1 = 0;
        ts.breakpoint2 = 0;
        return ts;
    }

    float clamped = std::min(speed_kmh, MAX_TRAFFIC_KPH);
    uint32_t encoded = static_cast<uint32_t>(std::round(clamped / 2.0f));
    if (encoded >= UNKNOWN_TRAFFIC_SPEED_RAW) encoded = UNKNOWN_TRAFFIC_SPEED_RAW - 1;
    if (encoded == 0 && clamped > 0.0f) encoded = 1; // 0 means "closed"

    uint32_t congestion = compute_congestion(speed_kmh, freeflow_kmh);

    // Uniform speed across the whole edge (single segment covering full length)
    ts.overall_encoded_speed = encoded;
    ts.encoded_speed1 = encoded;
    ts.encoded_speed2 = encoded;
    ts.encoded_speed3 = encoded;
    ts.breakpoint1 = 255;
    ts.breakpoint2 = 255;
    ts.congestion1 = congestion;
    ts.congestion2 = congestion;
    ts.congestion3 = congestion;
    ts.has_incidents = (speed_kmh < 5.0f) ? 1 : 0;
    ts.spare = 0;
    return ts;
}

static TrafficSpeed unknown_speed() {
    TrafficSpeed ts{};
    ts.overall_encoded_speed = UNKNOWN_TRAFFIC_SPEED_RAW;
    ts.encoded_speed1 = UNKNOWN_TRAFFIC_SPEED_RAW;
    ts.encoded_speed2 = UNKNOWN_TRAFFIC_SPEED_RAW;
    ts.encoded_speed3 = UNKNOWN_TRAFFIC_SPEED_RAW;
    ts.breakpoint1 = 0;
    ts.breakpoint2 = 0;
    return ts;
}

// ── Main ─────────────────────────────────────────────────────────────────────

static void usage(const char* prog) {
    std::cerr
        << "Usage: " << prog << " --tiles <tiles.tar> --csv <live.csv> --way-edges <way_edges.txt> -o <traffic.tar>\n"
        << "\nBuilds a Valhalla-compatible traffic.tar from live speeds and a way→edge mapping.\n"
        << "\nRequired:\n"
        << "  --tiles <path>       Valhalla tile extract (.tar with .gph tiles)\n"
        << "  --csv <path>         Live traffic CSV (way_id,speed_kmh,freeflow_kmh) from OpenTrafficDB\n"
        << "  --way-edges <path>   way_edges.txt from valhalla_ways_to_edges\n"
        << "  -o <path>            Output traffic.tar path\n"
        << "\nWorkflow:\n"
        << "  1. valhalla_ways_to_edges --config valhalla.json way_edges.txt\n"
        << "  2. curl -H 'Authorization: Bearer $TOKEN' $SERVER/api/v1/traffic/live/csv -o live.csv\n"
        << "  3. " << prog << " --tiles tiles.tar --csv live.csv --way-edges way_edges.txt -o traffic.tar\n";
}

int main(int argc, char* argv[]) {
    std::string tiles_path, csv_path, way_edges_path, output_path;

    for (int i = 1; i < argc; i++) {
        std::string arg = argv[i];
        if (arg == "--tiles" && i + 1 < argc)
            tiles_path = argv[++i];
        else if (arg == "--csv" && i + 1 < argc)
            csv_path = argv[++i];
        else if (arg == "--way-edges" && i + 1 < argc)
            way_edges_path = argv[++i];
        else if (arg == "-o" && i + 1 < argc)
            output_path = argv[++i];
        else if (arg == "-h" || arg == "--help") {
            usage(argv[0]);
            return 0;
        } else {
            std::cerr << "Unknown argument: " << arg << "\n";
            usage(argv[0]);
            return 1;
        }
    }

    if (tiles_path.empty() || csv_path.empty() || way_edges_path.empty() || output_path.empty()) {
        usage(argv[0]);
        return 1;
    }

    // ── 1. Parse inputs ──────────────────────────────────────────────────────

    auto live_speeds = parse_csv(csv_path);
    auto tile_edges  = parse_way_edges(way_edges_path);

    // ── 2. Scan tile extract for .gph tiles and read edge counts ─────────────

    std::cerr << "Scanning tile extract: " << tiles_path << "\n";
    auto tile_members = scan_tar(tiles_path);
    std::cerr << "Found " << tile_members.size() << " graph tiles\n";

    if (tile_members.empty()) {
        std::cerr << "No .gph tiles found in " << tiles_path << "\n";
        return 1;
    }

    std::ifstream tile_tar(tiles_path, std::ios::binary);

    struct TileInfo {
        std::string path;
        uint32_t    tile_key;
        uint32_t    edge_count;
    };
    std::vector<TileInfo> tiles;
    tiles.reserve(tile_members.size());

    for (auto& m : tile_members) {
        tile_tar.seekg(static_cast<std::streamoff>(m.data_offset + GRAPHTILE_SKIP_BYTES));
        GraphTileBitfield bf{};
        tile_tar.read(reinterpret_cast<char*>(&bf), sizeof(bf));
        tiles.push_back({m.name, tile_path_to_key(m.name),
                         static_cast<uint32_t>(bf.directededgecount)});
    }
    tile_tar.close();

    std::sort(tiles.begin(), tiles.end(),
              [](const TileInfo& a, const TileInfo& b) { return a.path < b.path; });

    // ── 3. Build per-tile speed lookup: tile_key → (edge_offset → LiveSpeed) ──

    // For each tile that has mappings, resolve OSM way speeds to edge offsets.
    std::unordered_map<uint32_t, std::unordered_map<uint32_t, LiveSpeed>> tile_speed_map;

    size_t matched_edges = 0;
    std::unordered_map<int64_t, bool> matched_ways;

    for (auto& [tile_key, edges] : tile_edges) {
        for (auto& em : edges) {
            auto it = live_speeds.find(em.osm_way_id);
            if (it != live_speeds.end()) {
                tile_speed_map[tile_key][em.edge_offset] = it->second;
                matched_edges++;
                matched_ways[em.osm_way_id] = true;
            }
        }
    }

    size_t unmatched_ways = live_speeds.size() - matched_ways.size();
    std::cerr << "Matched " << matched_edges << " directed edges from "
              << matched_ways.size() << " ways with live speeds\n";
    if (unmatched_ways > 0)
        std::cerr << "Warning: " << unmatched_ways
                  << " CSV way IDs had no entry in way_edges.txt\n";

    // ── 4. Write traffic.tar ─────────────────────────────────────────────────

    std::cerr << "Building traffic.tar: " << output_path << "\n";

    size_t index_size = tiles.size() * sizeof(IndexEntry);

    std::ofstream out(output_path, std::ios::binary);
    if (!out) {
        std::cerr << "Cannot create output: " << output_path << "\n";
        return 1;
    }

    // Write index.bin placeholder
    auto idx_hdr = make_tar_header("index.bin", index_size);
    out.write(reinterpret_cast<const char*>(&idx_hdr), sizeof(idx_hdr));

    std::vector<uint8_t> index_placeholder(index_size, 0);
    out.write(reinterpret_cast<const char*>(index_placeholder.data()),
              static_cast<std::streamsize>(index_size));

    size_t idx_pad = pad_to_block(index_size);
    if (idx_pad > 0) {
        std::vector<uint8_t> zeros(idx_pad, 0);
        out.write(reinterpret_cast<const char*>(zeros.data()),
                  static_cast<std::streamsize>(idx_pad));
    }

    TrafficSpeed default_ts = unknown_speed();
    std::vector<IndexEntry> index_entries;
    index_entries.reserve(tiles.size());

    uint64_t now = static_cast<uint64_t>(std::time(nullptr));
    size_t total_edges = 0;
    size_t total_live_edges = 0;

    for (auto& ti : tiles) {
        size_t traffic_data_size = sizeof(TrafficTileHeader) +
                                   ti.edge_count * sizeof(TrafficSpeed);

        auto member_hdr = make_tar_header(ti.path, traffic_data_size);
        size_t data_offset = static_cast<size_t>(out.tellp()) + sizeof(TarHeader);
        out.write(reinterpret_cast<const char*>(&member_hdr), sizeof(member_hdr));

        // Write TrafficTileHeader
        TrafficTileHeader th{};
        th.tile_id = ti.tile_key;
        th.last_update = now;
        th.directed_edge_count = ti.edge_count;
        th.traffic_tile_version = TRAFFIC_TILE_VERSION;
        out.write(reinterpret_cast<const char*>(&th), sizeof(th));

        // Write per-edge TrafficSpeed entries
        auto speed_it = tile_speed_map.find(ti.tile_key);

        for (uint32_t e = 0; e < ti.edge_count; e++) {
            if (speed_it != tile_speed_map.end()) {
                auto edge_it = speed_it->second.find(e);
                if (edge_it != speed_it->second.end()) {
                    auto& ls = edge_it->second;
                    auto ts = encode_speed(ls.speed_kmh, ls.freeflow_kmh);
                    out.write(reinterpret_cast<const char*>(&ts), sizeof(ts));
                    total_live_edges++;
                    continue;
                }
            }
            out.write(reinterpret_cast<const char*>(&default_ts), sizeof(default_ts));
        }

        total_edges += ti.edge_count;
        index_entries.push_back({data_offset, ti.tile_key,
                                 static_cast<uint32_t>(traffic_data_size)});

        size_t data_pad = pad_to_block(traffic_data_size);
        if (data_pad > 0) {
            std::vector<uint8_t> zeros(data_pad, 0);
            out.write(reinterpret_cast<const char*>(zeros.data()),
                      static_cast<std::streamsize>(data_pad));
        }
    }

    // End-of-archive marker (two zero blocks)
    std::vector<uint8_t> end_marker(TAR_BLOCK * 2, 0);
    out.write(reinterpret_cast<const char*>(end_marker.data()),
              static_cast<std::streamsize>(end_marker.size()));
    out.close();

    // ── 5. Patch index.bin with real offsets ──────────────────────────────────

    std::fstream patch(output_path, std::ios::in | std::ios::out | std::ios::binary);
    patch.seekp(TAR_BLOCK); // index.bin data starts after its tar header
    for (auto& ie : index_entries) {
        patch.write(reinterpret_cast<const char*>(&ie.offset), sizeof(ie.offset));
        patch.write(reinterpret_cast<const char*>(&ie.tile_id), sizeof(ie.tile_id));
        patch.write(reinterpret_cast<const char*>(&ie.size), sizeof(ie.size));
    }
    patch.close();

    // ── Done ─────────────────────────────────────────────────────────────────

    std::cerr << "Done. " << tiles.size() << " tiles, "
              << total_edges << " total edges, "
              << total_live_edges << " with live speed ("
              << (total_edges > 0 ? 100.0 * total_live_edges / total_edges : 0.0)
              << "%)\n";

    return 0;
}
