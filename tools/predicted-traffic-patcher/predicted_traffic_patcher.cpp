// predicted_traffic_patcher.cpp
//
// Patches Valhalla graph tiles (.gph) inside valhalla_tiles.tar with predicted
// traffic data from the OpenTrafficDB valhalla/csv export.
//
// For each directed edge that has matching data, this tool:
//   - Sets free_flow_speed and constrained_flow_speed in the DirectedEdge
//   - Appends DCT-II compressed predicted speed profiles to the tile
//   - Updates the GraphTileHeader offsets accordingly
//
// Inputs:
//   1. valhalla_tiles.tar   — Valhalla tile extract (.tar with .gph tiles)
//   2. predicted.csv        — From OpenTrafficDB /api/v1/traffic/valhalla/csv
//                             Format: way_id,freeflow_speed,constrained_speed,historical_speeds
//   3. way_edges.txt        — From valhalla_ways_to_edges
//
// Output:
//   A new valhalla_tiles.tar with patched .gph tiles.
//
// Build:
//   g++ -std=c++17 -O2 -o predicted_traffic_patcher predicted_traffic_patcher.cpp
//
// Usage:
//   ./predicted_traffic_patcher --tiles valhalla_tiles.tar --csv predicted.csv \
//       --way-edges way_edges.txt -o valhalla_tiles_patched.tar

#include <algorithm>
#include <array>
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

// ── Valhalla constants ───────────────────────────────────────────────────────

static constexpr uint32_t kCoefficientCount       = 200;
static constexpr size_t   kGraphTileHeaderSize     = 272;
static constexpr size_t   kNodeInfoSize            = 32;
static constexpr size_t   kNodeTransitionSize      = 8;
static constexpr size_t   kDirectedEdgeSize        = 48;

// Byte offsets within GraphTileHeader
static constexpr size_t   kHeaderCountsBitfield    = 40;   // nodecount(21)|directededgecount(21)|predictedspeeds_count(21)|spare(1)
static constexpr size_t   kHeaderTransitionCount   = 48;   // transitioncount(22)|spare(10) as uint32_t
static constexpr size_t   kHeaderPredSpeedsOffset  = 220;  // uint32_t predictedspeeds_offset_
static constexpr size_t   kHeaderTileSize          = 224;   // uint32_t tile_size_ (end offset)

// Byte offsets within DirectedEdge (48 bytes)
// 3rd 8-byte word (bytes 16-23):
//   speed(8)|free_flow_speed(8)|constrained_flow_speed(8)|truck_speed(8)|
//   name_consistency(8)|use(6)|lanecount(4)|density(4)|classification(3)|
//   surface(3)|toll(1)|roundabout(1)|truck_route(1)|has_predicted_speed(1)
static constexpr size_t   kDE_FreeFlowSpeed        = 17;   // byte offset within DirectedEdge
static constexpr size_t   kDE_ConstrainedFlowSpeed = 18;
static constexpr size_t   kDE_HasPredictedByte     = 23;   // has_predicted_speed_ is bit 7
static constexpr uint8_t  kDE_HasPredictedMask     = 0x80;

// ── Valhalla GraphId decomposition ───────────────────────────────────────────

static constexpr uint64_t GRAPHID_LEVEL_BITS = 3;
static constexpr uint64_t GRAPHID_TILE_BITS  = 22;
static constexpr uint64_t GRAPHID_ID_BITS    = 21;
static constexpr uint64_t GRAPHID_LEVEL_MASK = (1ULL << GRAPHID_LEVEL_BITS) - 1;
static constexpr uint64_t GRAPHID_TILE_MASK  = (1ULL << GRAPHID_TILE_BITS) - 1;
static constexpr uint64_t GRAPHID_ID_MASK    = (1ULL << GRAPHID_ID_BITS) - 1;

struct GraphId {
    uint32_t level;
    uint32_t tile_id;
    uint32_t edge_id;
    uint32_t tile_key() const { return level | (tile_id << GRAPHID_LEVEL_BITS); }
};

static GraphId decode_graph_id(uint64_t raw) {
    return {
        static_cast<uint32_t>(raw & GRAPHID_LEVEL_MASK),
        static_cast<uint32_t>((raw >> GRAPHID_LEVEL_BITS) & GRAPHID_TILE_MASK),
        static_cast<uint32_t>((raw >> (GRAPHID_LEVEL_BITS + GRAPHID_TILE_BITS)) & GRAPHID_ID_MASK)
    };
}

// ── Tar format ───────────────────────────────────────────────────────────────

static constexpr size_t TAR_BLOCK = 512;

struct TarHeader {
    char name[100]; char mode[8]; char uid[8]; char gid[8];
    char size[12]; char mtime[12]; char checksum[8]; char typeflag;
    char linkname[100]; char magic[6]; char version[2]; char uname[32];
    char gname[32]; char devmajor[8]; char devminor[8]; char prefix[155];
    char padding[12];
};
static_assert(sizeof(TarHeader) == 512, "TarHeader must be 512 bytes");

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
    if (!f) { std::cerr << "Cannot open tile tar: " << tar_path << "\n"; std::exit(1); }

    std::vector<TarMember> members;
    TarHeader hdr{};
    while (f.read(reinterpret_cast<char*>(&hdr), sizeof(hdr))) {
        if (hdr.name[0] == '\0') break;
        std::string name(hdr.name);
        while (!name.empty() && name.back() == '\0') name.pop_back();
        size_t size = std::strtoull(hdr.size, nullptr, 8);
        size_t data_offset = static_cast<size_t>(f.tellg());
        members.push_back({name, data_offset, size});
        f.seekg(static_cast<std::streamoff>(size + pad_to_block(size)), std::ios::cur);
    }
    return members;
}

// ── Tile path → tile key ─────────────────────────────────────────────────────

static uint32_t tile_path_to_key(const std::string& path) {
    auto dot = path.rfind('.');
    std::string no_ext = (dot != std::string::npos) ? path.substr(0, dot) : path;
    auto slash = no_ext.find('/');
    if (slash == std::string::npos) {
        std::cerr << "Invalid tile path: " << path << "\n"; std::exit(1);
    }
    uint32_t level = std::stoul(no_ext.substr(0, slash));
    std::string idx_str;
    for (size_t i = slash + 1; i < no_ext.size(); i++)
        if (no_ext[i] != '/') idx_str += no_ext[i];
    uint32_t idx = std::stoul(idx_str);
    return level | (idx << GRAPHID_LEVEL_BITS);
}

// ── Base64 decoding ──────────────────────────────────────────────────────────

static const int8_t b64_table[256] = {
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,62,-1,-1,-1,63,
    52,53,54,55,56,57,58,59,60,61,-1,-1,-1,-1,-1,-1,
    -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,
    15,16,17,18,19,20,21,22,23,24,25,-1,-1,-1,-1,-1,
    -1,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,
    41,42,43,44,45,46,47,48,49,50,51,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
};

static std::vector<uint8_t> base64_decode(const std::string& input) {
    std::vector<uint8_t> out;
    out.reserve(input.size() * 3 / 4);
    uint32_t accum = 0;
    int bits = 0;
    for (unsigned char c : input) {
        if (c == '=' || c == '\n' || c == '\r') continue;
        int val = b64_table[c];
        if (val < 0) continue;
        accum = (accum << 6) | static_cast<uint32_t>(val);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out.push_back(static_cast<uint8_t>((accum >> bits) & 0xFF));
        }
    }
    return out;
}

// ── Predicted speed data per way ─────────────────────────────────────────────

struct PredictedSpeed {
    uint8_t freeflow_speed;
    uint8_t constrained_speed;
    bool has_coefficients;
    std::array<int16_t, kCoefficientCount> coefficients;
};

// ── Parse valhalla/csv ───────────────────────────────────────────────────────

static std::unordered_map<int64_t, PredictedSpeed> parse_csv(const std::string& csv_path) {
    std::ifstream f(csv_path);
    if (!f) { std::cerr << "Cannot open CSV: " << csv_path << "\n"; std::exit(1); }

    std::unordered_map<int64_t, PredictedSpeed> speeds;
    std::string line;
    std::getline(f, line); // skip header

    size_t line_num = 1;
    while (std::getline(f, line)) {
        line_num++;
        if (line.empty()) continue;

        // Format: way_id,freeflow_speed,constrained_speed,historical_speeds
        auto c1 = line.find(',');
        if (c1 == std::string::npos) continue;
        auto c2 = line.find(',', c1 + 1);
        if (c2 == std::string::npos) continue;
        auto c3 = line.find(',', c2 + 1);
        if (c3 == std::string::npos) continue;

        PredictedSpeed ps{};
        try {
            int64_t way_id     = std::stoll(line.substr(0, c1));
            int freeflow       = std::stoi(line.substr(c1 + 1, c2 - c1 - 1));
            int constrained    = std::stoi(line.substr(c2 + 1, c3 - c2 - 1));
            std::string b64    = line.substr(c3 + 1);

            ps.freeflow_speed    = static_cast<uint8_t>(std::clamp(freeflow, 0, 255));
            ps.constrained_speed = static_cast<uint8_t>(std::clamp(constrained, 0, 255));

            // Decode Base64 → big-endian int16 coefficients → native int16
            if (!b64.empty()) {
                auto bytes = base64_decode(b64);
                if (bytes.size() == kCoefficientCount * 2) {
                    ps.has_coefficients = true;
                    for (uint32_t i = 0; i < kCoefficientCount; i++) {
                        // Big-endian to native
                        int16_t val = static_cast<int16_t>(
                            (static_cast<uint16_t>(bytes[i * 2]) << 8) |
                             static_cast<uint16_t>(bytes[i * 2 + 1]));
                        ps.coefficients[i] = val;
                    }
                } else {
                    std::cerr << "Warning: line " << line_num
                              << " has invalid coefficient size (" << bytes.size()
                              << " bytes, expected " << kCoefficientCount * 2 << ")\n";
                }
            }

            speeds[way_id] = ps;
        } catch (const std::exception& e) {
            std::cerr << "Warning: failed to parse CSV line " << line_num << ": " << e.what() << "\n";
        }
    }

    std::cerr << "Loaded " << speeds.size() << " predicted speeds from CSV\n";
    return speeds;
}

// ── Parse way_edges.txt ──────────────────────────────────────────────────────

struct EdgeMapping {
    uint32_t edge_offset;
    int64_t  osm_way_id;
};

static std::unordered_map<uint32_t, std::vector<EdgeMapping>>
parse_way_edges(const std::string& path) {
    std::ifstream f(path);
    if (!f) { std::cerr << "Cannot open way_edges file: " << path << "\n"; std::exit(1); }

    std::unordered_map<uint32_t, std::vector<EdgeMapping>> tile_edges;
    std::string line;
    size_t total_mappings = 0;

    while (std::getline(f, line)) {
        if (line.empty()) continue;
        std::istringstream ss(line);
        std::string token;

        if (!std::getline(ss, token, ',')) continue;
        int64_t way_id = std::stoll(token);

        while (std::getline(ss, token, ',')) {
            // direction (skip)
            if (!std::getline(ss, token, ',')) break;
            uint64_t raw_graph_id = std::stoull(token);

            auto gid = decode_graph_id(raw_graph_id);
            tile_edges[gid.tile_key()].push_back({gid.edge_id, way_id});
            total_mappings++;
        }
    }

    std::cerr << "Loaded " << total_mappings << " edge mappings across "
              << tile_edges.size() << " tiles\n";
    return tile_edges;
}

// ── Read helpers for tile header ─────────────────────────────────────────────

static uint32_t read_u32(const char* data, size_t offset) {
    uint32_t v;
    std::memcpy(&v, data + offset, sizeof(v));
    return v;
}

static uint64_t read_u64(const char* data, size_t offset) {
    uint64_t v;
    std::memcpy(&v, data + offset, sizeof(v));
    return v;
}

static void write_u32(char* data, size_t offset, uint32_t val) {
    std::memcpy(data + offset, &val, sizeof(val));
}

static void write_u64(char* data, size_t offset, uint64_t val) {
    std::memcpy(data + offset, &val, sizeof(val));
}

struct TileHeaderInfo {
    uint32_t nodecount;
    uint32_t directededgecount;
    uint32_t predictedspeeds_count;
    uint32_t transitioncount;
    uint32_t predictedspeeds_offset;
    uint32_t tile_size;
    size_t   de_byte_offset; // offset to DirectedEdge array within tile data
};

static TileHeaderInfo parse_tile_header(const char* data) {
    TileHeaderInfo info{};

    // Counts bitfield at offset 40: nodecount(21)|directededgecount(21)|predictedspeeds_count(21)|spare(1)
    uint64_t counts = read_u64(data, kHeaderCountsBitfield);
    info.nodecount             = static_cast<uint32_t>(counts & 0x1FFFFF);
    info.directededgecount     = static_cast<uint32_t>((counts >> 21) & 0x1FFFFF);
    info.predictedspeeds_count = static_cast<uint32_t>((counts >> 42) & 0x1FFFFF);

    // Transition count at offset 48: transitioncount(22)|spare(10) as uint32_t
    uint32_t trans_word = read_u32(data, kHeaderTransitionCount);
    info.transitioncount = trans_word & 0x3FFFFF;

    info.predictedspeeds_offset = read_u32(data, kHeaderPredSpeedsOffset);
    info.tile_size              = read_u32(data, kHeaderTileSize);

    // DirectedEdges start after header + nodes + transitions
    info.de_byte_offset = kGraphTileHeaderSize +
                          info.nodecount * kNodeInfoSize +
                          info.transitioncount * kNodeTransitionSize;

    return info;
}

// ── Update header fields in tile data ────────────────────────────────────────

static void update_tile_header(char* data,
                               uint32_t new_predspeeds_count,
                               uint32_t new_predspeeds_offset,
                               uint32_t new_tile_size) {
    // Update predictedspeeds_count in the counts bitfield
    uint64_t counts = read_u64(data, kHeaderCountsBitfield);
    // Clear bits 42-62 (predictedspeeds_count) and set new value
    counts &= ~(uint64_t{0x1FFFFF} << 42);
    counts |= (static_cast<uint64_t>(new_predspeeds_count) & 0x1FFFFF) << 42;
    write_u64(data, kHeaderCountsBitfield, counts);

    write_u32(data, kHeaderPredSpeedsOffset, new_predspeeds_offset);
    write_u32(data, kHeaderTileSize, new_tile_size);
}

// ── Index.bin entry (matches valhalla_build_extract format) ──────────────────

struct IndexEntry {
    uint64_t offset;
    uint32_t tile_id;
    uint32_t size;
};

// ── Main ─────────────────────────────────────────────────────────────────────

static void usage(const char* prog) {
    std::cerr
        << "Usage: " << prog << " --tiles <tiles.tar> --csv <predicted.csv> --way-edges <way_edges.txt> -o <output.tar>\n"
        << "\nPatches Valhalla graph tiles with predicted traffic data.\n"
        << "\nRequired:\n"
        << "  --tiles <path>       Valhalla tile extract (.tar with .gph tiles)\n"
        << "  --csv <path>         Predicted traffic CSV from OpenTrafficDB valhalla/csv endpoint\n"
        << "                       Format: way_id,freeflow_speed,constrained_speed,historical_speeds\n"
        << "  --way-edges <path>   way_edges.txt from valhalla_ways_to_edges\n"
        << "  -o <path>            Output .tar path (patched tiles)\n"
        << "\nWorkflow:\n"
        << "  1. valhalla_ways_to_edges --config valhalla.json way_edges.txt\n"
        << "  2. curl -H 'Authorization: Bearer $TOKEN' $SERVER/api/v1/traffic/valhalla/csv -o predicted.csv\n"
        << "  3. " << prog << " --tiles tiles.tar --csv predicted.csv --way-edges way_edges.txt -o patched_tiles.tar\n";
}

// Helper: write a tile (or any data) to the tar, return the data offset within the tar
static size_t write_tar_member(std::ofstream& out, const std::string& name,
                               const char* data, size_t data_size) {
    auto hdr = make_tar_header(name, data_size);
    out.write(reinterpret_cast<const char*>(&hdr), sizeof(hdr));
    size_t data_offset = static_cast<size_t>(out.tellp());
    out.write(data, static_cast<std::streamsize>(data_size));
    size_t pad = pad_to_block(data_size);
    if (pad > 0) {
        std::vector<uint8_t> zeros(pad, 0);
        out.write(reinterpret_cast<const char*>(zeros.data()),
                  static_cast<std::streamsize>(pad));
    }
    return data_offset;
}

int main(int argc, char* argv[]) {
    std::string tiles_path, csv_path, way_edges_path, output_path;

    for (int i = 1; i < argc; i++) {
        std::string arg = argv[i];
        if (arg == "--tiles" && i + 1 < argc)       tiles_path = argv[++i];
        else if (arg == "--csv" && i + 1 < argc)    csv_path = argv[++i];
        else if (arg == "--way-edges" && i + 1 < argc) way_edges_path = argv[++i];
        else if (arg == "-o" && i + 1 < argc)       output_path = argv[++i];
        else if (arg == "-h" || arg == "--help")     { usage(argv[0]); return 0; }
        else { std::cerr << "Unknown argument: " << arg << "\n"; usage(argv[0]); return 1; }
    }

    if (tiles_path.empty() || csv_path.empty() || way_edges_path.empty() || output_path.empty()) {
        usage(argv[0]);
        return 1;
    }

    // ── 1. Parse inputs ──────────────────────────────────────────────────────

    auto predicted_speeds = parse_csv(csv_path);
    auto tile_edges       = parse_way_edges(way_edges_path);

    // ── 2. Scan tile extract ─────────────────────────────────────────────────

    std::cerr << "Scanning tile extract: " << tiles_path << "\n";
    auto all_members = scan_tar(tiles_path);
    std::cerr << "Found " << all_members.size() << " entries in tar\n";

    if (all_members.empty()) {
        std::cerr << "No entries found in " << tiles_path << "\n";
        return 1;
    }

    // Separate .gph tiles from other entries (like index.bin)
    // We skip index.bin from input — we'll rebuild it from scratch.
    std::vector<TarMember> gph_members;
    std::vector<TarMember> other_members;
    for (auto& m : all_members) {
        bool is_gph = m.name.size() > 4 && m.name.substr(m.name.size() - 4) == ".gph";
        if (is_gph)
            gph_members.push_back(m);
        else if (m.name != "index.bin")
            other_members.push_back(m);
    }

    std::cerr << "Found " << gph_members.size() << " graph tiles\n";

    // ── 3. Build per-tile speed lookup ───────────────────────────────────────

    // tile_key → (edge_offset → PredictedSpeed)
    std::unordered_map<uint32_t, std::unordered_map<uint32_t, const PredictedSpeed*>> tile_speed_map;

    size_t matched_edges = 0;
    std::unordered_map<int64_t, bool> matched_ways;

    for (auto& [tile_key, edges] : tile_edges) {
        for (auto& em : edges) {
            auto it = predicted_speeds.find(em.osm_way_id);
            if (it != predicted_speeds.end()) {
                tile_speed_map[tile_key][em.edge_offset] = &it->second;
                matched_edges++;
                matched_ways[em.osm_way_id] = true;
            }
        }
    }

    size_t unmatched_ways = predicted_speeds.size() - matched_ways.size();
    std::cerr << "Matched " << matched_edges << " directed edges from "
              << matched_ways.size() << " ways\n";
    if (unmatched_ways > 0)
        std::cerr << "Warning: " << unmatched_ways
                  << " CSV way IDs had no entry in way_edges.txt\n";

    // ── 4. Process tiles and write output ────────────────────────────────────

    std::cerr << "Patching tiles into: " << output_path << "\n";

    std::ifstream tile_tar(tiles_path, std::ios::binary);
    std::ofstream out(output_path, std::ios::binary);
    if (!out) { std::cerr << "Cannot create output: " << output_path << "\n"; return 1; }

    // Write index.bin placeholder first (we'll patch it at the end)
    size_t index_entry_count = gph_members.size();
    size_t index_size = index_entry_count * sizeof(IndexEntry);
    {
        auto idx_hdr = make_tar_header("index.bin", index_size);
        out.write(reinterpret_cast<const char*>(&idx_hdr), sizeof(idx_hdr));
        std::vector<uint8_t> placeholder(index_size, 0);
        out.write(reinterpret_cast<const char*>(placeholder.data()),
                  static_cast<std::streamsize>(index_size));
        size_t pad = pad_to_block(index_size);
        if (pad > 0) {
            std::vector<uint8_t> zeros(pad, 0);
            out.write(reinterpret_cast<const char*>(zeros.data()),
                      static_cast<std::streamsize>(pad));
        }
    }

    // Write non-.gph, non-index.bin members through unchanged
    for (auto& m : other_members) {
        std::vector<char> data(m.size);
        tile_tar.seekg(static_cast<std::streamoff>(m.data_offset));
        tile_tar.read(data.data(), static_cast<std::streamsize>(m.size));
        write_tar_member(out, m.name, data.data(), data.size());
    }

    // Process .gph tiles, collecting index entries
    std::vector<IndexEntry> index_entries;
    index_entries.reserve(gph_members.size());

    size_t total_patched_edges = 0;
    size_t total_predicted_profiles = 0;
    size_t tiles_modified = 0;

    for (auto& m : gph_members) {
        // Read the tile data
        std::vector<char> tile_data(m.size);
        tile_tar.seekg(static_cast<std::streamoff>(m.data_offset));
        tile_tar.read(tile_data.data(), static_cast<std::streamsize>(m.size));

        uint32_t tile_key = tile_path_to_key(m.name);
        auto speed_it = tile_speed_map.find(tile_key);

        if (speed_it == tile_speed_map.end() || speed_it->second.empty()) {
            // No data for this tile — pass through unchanged
            size_t data_off = write_tar_member(out, m.name, tile_data.data(), tile_data.size());
            index_entries.push_back({data_off, tile_key, static_cast<uint32_t>(tile_data.size())});
            continue;
        }

        // Parse the tile header
        if (tile_data.size() < kGraphTileHeaderSize) {
            std::cerr << "Warning: tile " << m.name << " too small, skipping\n";
            size_t data_off = write_tar_member(out, m.name, tile_data.data(), tile_data.size());
            index_entries.push_back({data_off, tile_key, static_cast<uint32_t>(tile_data.size())});
            continue;
        }

        auto info = parse_tile_header(tile_data.data());

        // Determine base tile size (without existing predicted speeds)
        uint32_t base_tile_size;
        if (info.predictedspeeds_count > 0 && info.predictedspeeds_offset > 0 &&
            info.predictedspeeds_offset <= info.tile_size) {
            base_tile_size = info.predictedspeeds_offset;
        } else {
            base_tile_size = info.tile_size;
        }

        // Validate DE offset
        size_t de_end = info.de_byte_offset + info.directededgecount * kDirectedEdgeSize;
        if (de_end > base_tile_size) {
            std::cerr << "Warning: tile " << m.name
                      << " DirectedEdge array exceeds tile bounds, passing through\n";
            size_t data_off = write_tar_member(out, m.name, tile_data.data(), tile_data.size());
            index_entries.push_back({data_off, tile_key, static_cast<uint32_t>(tile_data.size())});
            continue;
        }

        // ── Patch DirectedEdge entries ───────────────────────────────────────

        auto& edge_speeds = speed_it->second;

        // Build predicted speed offset table and profiles
        std::vector<uint32_t> offset_table(info.directededgecount, 0);
        std::vector<int16_t> profiles;
        profiles.reserve(edge_speeds.size() * kCoefficientCount);

        uint32_t pred_count = 0;

        for (uint32_t e = 0; e < info.directededgecount; e++) {
            auto eit = edge_speeds.find(e);
            if (eit == edge_speeds.end()) continue;

            const PredictedSpeed* ps = eit->second;
            size_t de_offset = info.de_byte_offset + e * kDirectedEdgeSize;

            // Set free_flow_speed
            if (ps->freeflow_speed > 0) {
                tile_data[de_offset + kDE_FreeFlowSpeed] =
                    static_cast<char>(ps->freeflow_speed);
            }

            // Set constrained_flow_speed
            if (ps->constrained_speed > 0) {
                tile_data[de_offset + kDE_ConstrainedFlowSpeed] =
                    static_cast<char>(ps->constrained_speed);
            }

            // Set predicted speed profile
            if (ps->has_coefficients) {
                offset_table[e] = static_cast<uint32_t>(profiles.size());
                profiles.insert(profiles.end(),
                                ps->coefficients.begin(), ps->coefficients.end());

                // Set has_predicted_speed_ flag (bit 7 of byte 23 within DirectedEdge)
                tile_data[de_offset + kDE_HasPredictedByte] |=
                    static_cast<char>(kDE_HasPredictedMask);

                pred_count++;
            }

            total_patched_edges++;
        }

        total_predicted_profiles += pred_count;

        // ── Build the new tile ───────────────────────────────────────────────

        size_t offset_table_bytes = info.directededgecount * sizeof(uint32_t);
        size_t profiles_bytes     = profiles.size() * sizeof(int16_t);
        size_t pred_section_size  = offset_table_bytes + profiles_bytes;

        uint32_t new_predspeeds_offset = base_tile_size;
        uint32_t new_tile_size         = base_tile_size + static_cast<uint32_t>(pred_section_size);

        // Update header in tile data
        update_tile_header(tile_data.data(), pred_count, new_predspeeds_offset, new_tile_size);

        // Write tar header for the new tile size
        size_t total_tile_bytes = new_tile_size;
        auto hdr = make_tar_header(m.name, total_tile_bytes);
        out.write(reinterpret_cast<const char*>(&hdr), sizeof(hdr));
        size_t data_off = static_cast<size_t>(out.tellp());

        // Write base tile data (up to predictedspeeds_offset)
        out.write(tile_data.data(), static_cast<std::streamsize>(base_tile_size));

        // Write predicted speeds: offset table then profiles
        out.write(reinterpret_cast<const char*>(offset_table.data()),
                  static_cast<std::streamsize>(offset_table_bytes));
        if (!profiles.empty()) {
            out.write(reinterpret_cast<const char*>(profiles.data()),
                      static_cast<std::streamsize>(profiles_bytes));
        }

        // Pad to tar block boundary
        size_t pad = pad_to_block(total_tile_bytes);
        if (pad > 0) {
            std::vector<uint8_t> zeros(pad, 0);
            out.write(reinterpret_cast<const char*>(zeros.data()),
                      static_cast<std::streamsize>(pad));
        }

        index_entries.push_back({data_off, tile_key, static_cast<uint32_t>(total_tile_bytes)});
        tiles_modified++;
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

    std::cerr << "Done. " << gph_members.size() << " tiles processed, "
              << tiles_modified << " modified, "
              << total_patched_edges << " edges patched ("
              << total_predicted_profiles << " with predicted speed profiles)\n";

    return 0;
}
