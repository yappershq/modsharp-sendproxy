# Encoder value-pointer conventions (libnetworksystem.so)

Every per-field encoder has the ABI `enc(rdi=bf_write*, rsi=fieldInfo*, rdx=paramsPtr,
rcx=valuePtr, r8d=extra) -> ret`. What differs per encoder is **how it reads `valuePtr` (rcx)**.
A substitution must build the scratch in exactly that layout or the wire is garbage. File
offsets; Ghidra addr = file + 0x100000.

| Bucket / name | file | reads valuePtr as | our scratch | OK? |
|---|---|---|---|---|
| b1 int `default` | 0x3c8e70 | `*(long*)`, zigzag→varint | `*(long*)=intBits` (sign-ext) | ✓ |
| b1 `fixed32` | 0x3c3b10 | `*(u32)` raw 32 | long, low 32 | ✓ |
| b1 `fixed64` | 0x3c40c0 | `*(u32)` + `[1]` = 64 bits | `*(long*)` | ✓ (int carrier ⇒ ≤32-bit values) |
| b2 uint `default` | 0x3c8e90 | `*(u64)` varint | `*(ulong*)=(uint)intBits` | ✓ |
| b2 `fixed32` | 0x3c3df0 | `*(u32)` | long low 32 | ✓ |
| b2 `fixed64` | 0x3c4660 | `*(u32)`+`[1]` 64 | `*(long*)` | ✓ |
| b3 `quantized`(default) | 0x3c4d70 | **struct**: floats `[i]`, **count @+0x28** | see note | uniform ✗→fixed, per-client ✓ |
| b3 `qangle` | 0x3c6220 | `[0..2]` 3 floats (+ `*params` bitcount) | 3 floats | ✓ |
| b3 `normal` | 0x3c5850 | `[0..2]` 3 floats | 3 floats | ✓ |
| b3 `coord` | 0x3cfb40 | **struct**: `[0],[3],[4],[5]` + **mode @+0x28** (`[10]`) | see note | uniform ✗→fixed, per-client ✓ |
| b3 `coord_integral` | 0x3d0d20 | **struct**: `[0..6]` + **mode @+0x28** (`[10]`) | see note | uniform ✗→fixed, per-client ✓ |
| b3 `qangle_pitch_yaw` | 0x3cf320 | `[0],[1]` 2 floats (+ `*params`) | 3 floats (reads 2) | ✓ |
| b3 `qangle_precise` | 0x3c9ed0 | `[0..2]` 3 floats | 3 floats | ✓ |
| b4 `float32` | 0x3ce8e0 | **`*(double*)`** (8 bytes!) | `*(double*)` | ✓ |
| b5 `string` | 0x3c8f50 | `*(char**)` → WriteString | slot→buf | ✓ (dangling-ptr fixed) |
| b6 `array` | 0x3d61e0 | `{+0x00 data*, +0x28 count}` | same | ✓ (dangling-ptr fixed) |
| b7 `bool` | 0x3c4c00 | `*(byte*)` | byte | ✓ |

## Notes / gotchas

- **float32 reads a `double`, not a `float`.** `dVar2 = *(double*)valuePtr`. Both the uniform and
  per-client paths store `*(double*)scratch = (double)floatValue` — correct. Storing a 4-byte float
  there would feed the encoder a malformed double.
- **The +0x28 struct encoders** (`quantized`, `coord`, `coord_integral`) read a component
  count/mode at `valuePtr+0x28` (`param_4[10]`) and read more than three float lanes. They cannot
  be driven by a bare 3-float scratch.
  - **Per-client** (`FieldSubstitution`): already correct — it copies the live value struct via
    `TryGetLiveValuePtr` (preserving +0x28 and all lanes) and patches the leading floats.
  - **Uniform** (`UniformEncoderHook`): previously wrote 3 floats into a non-zeroed scratch, leaving
    +0x28 as garbage → the encoder looped a garbage count and corrupted the delta. **Fixed**: copy
    the real value struct (the original `valuePtr` passed to the hook) into the scratch, then patch
    x/y/z. Best-effort for >3-lane encoders (extra lanes keep their real values; never corrupts).
- **String / byte-array payload lifetime.** The string (`*char**`) and byte-array (`{data,count}`)
  encoders dereference the value pointer *during the encode call*, so the payload buffer must live
  in the calling frame. A buffer `stackalloc`'d in a helper that returns before the encoder runs is
  already freed → garbage on the wire (this was the "weird symbols on fake names" bug). Both paths
  now allocate the payload in the same frame that invokes the encoder.
- **`m_iszPlayerName` is an inline `char[128]`** but is still encoded by the b5 `*(char**)`
  string encoder — the flattened serializer hands the encoder a pointer-to-pointer at encode time,
  so the char** convention is correct; the garble was purely the dangling-buffer bug above.

## Coverage summary

Correct in both paths: int/uint (`default`/`fixed32`/`fixed64`), bool, float32, qangle,
qangle_pitch_yaw, qangle_precise, normal, vector3, string, byte-array. Struct floats
(quantized/coord/coord_integral) correct per-client and now fixed for uniform. No remaining known
value-pointer mismatches.
