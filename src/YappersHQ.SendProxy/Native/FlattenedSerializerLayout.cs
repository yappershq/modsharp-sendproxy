namespace YappersHQ.SendProxy.Native;

/// <summary>
///     Reverse-engineered struct offsets for CS2's flattened-serializer network field
///     descriptor (<c>CNetworkSerializerFieldInfo</c>) and the per-field encode dispatch,
///     as observed in <c>libnetworksystem.so</c> (build 2026-06-02). See README for the
///     full RE writeup.
///
///     ⚠️ These offsets are build-specific and were derived from one server build. They MUST
///     be re-verified against the live binary before the encoder pointer is patched — that is
///     why <see cref="EncoderHook.Enabled"/> gates the live memory write. Prefer driving these
///     from the gamedata file (yappershq.sendproxy.jsonc) once stabilized.
/// </summary>
internal static class FlattenedSerializerLayout
{
    // ── CFlattenedSerializer object layout (VERIFIED in CFlattenedSerializer::WriteFieldList) ──
    /// <summary>Serializer's own name pointer (CUtlString/char*). Verified.</summary>
    public const int SerializerNameOffset = 0x00;
    /// <summary>Field count (int32). Verified.</summary>
    public const int SerializerFieldCountOffset = 0x08;
    /// <summary>Field array base: an array of POINTERS to field records (stride = 8). Verified.</summary>
    public const int SerializerFieldArrayOffset = 0x10;
    public const int FieldArrayStride = 8;

    // ── Per field-record layout ──
    /// <summary>Field record vtable ptr (record+0x00). Verified.</summary>
    public const int FieldRecordVTableOffset = 0x00;
    /// <summary>Field name lives in a sub-object reached via record+0x10. INFERRED — confirm on a live dump.</summary>
    public const int FieldRecordNameSubObjectOffset = 0x10;

    // REMAINING UNKNOWNS before the live patch is safe (see README Phase 1):
    //  - entity/class -> CFlattenedSerializer accessor (registry lookup @ ~0x4901a6, not yet RE'd).
    //  - confirm the +0x10 field-array record IS the same object EncodeField receives as `fieldInfo`
    //    (so EncoderDispatchOffset 0x38 applies to it) — needs decompiling CFlattenedSerializer::Encode.
    //  - confirm FieldRecordNameSubObjectOffset for name matching.

    /// <summary>
    ///     Offset within CNetworkSerializerFieldInfo of the pointer to the encoder dispatch
    ///     object. The actual per-field encode function is vtable slot 0 of that object —
    ///     i.e. <c>**(void***)(fieldInfo + EncoderDispatchOffset)</c>. This is the Source2
    ///     analog of Source1's <c>SendProp::m_pProxyFn</c> and the swap target for a hook.
    /// </summary>
    public const int EncoderDispatchOffset = 0x38;

    /// <summary>
    ///     Offset of the field's value within the owning entity instance (int32). The encode
    ///     function reads the value from <c>entityBase + m_nFieldOffset</c> (with a secondary
    ///     byte adjust at +0xC9 when present — see EncodeField decomp).
    /// </summary>
    public const int FieldValueOffset = 0x40;

    /// <summary>Secondary field-offset adjust byte (0xFF = none). From EncodeField line ~330.</summary>
    public const int FieldValueAdjustByte = 0xC9;

    /// <summary>Per-field send-proxy recipients filter (shared_ptr&lt;NetworkRecipientsFilter_t&gt;). Phase 2.</summary>
    public const int RecipientsFilterOffset = 0x28;
}

/// <summary>
///     Reconstructed signature of the per-field encode function (encoder dispatch vtable slot 0):
///     <code>void EncodeFieldFn(bf_write* buf, CNetworkSerializerFieldInfo* field, void* valuePtr, void* ctx, uint unk)</code>
///     A hook trampoline reads <c>valuePtr</c> (the real value in entity memory), invokes the
///     managed callback (which may overwrite via ref), then calls the original encoder with the
///     possibly-modified value.
/// </summary>
internal static class EncoderHook
{
    /// <summary>
    ///     Master gate for the live memory patch. Stays <c>false</c> until the offsets above are
    ///     verified against the running binary on a test server. With this false the module loads
    ///     and the registry/API work, but no engine memory is modified (no crash risk).
    /// </summary>
    public static readonly bool Enabled = false;
}
