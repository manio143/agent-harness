// NOTE: Historical patch file for NJsonSchema generation gaps.
//
// Keep this file present (stable compilation unit) but aim to keep it empty.
// If new gaps appear, prefer:
// 1) UnionGen for discriminated unions, or
// 2) CodegenPostProcessor schema-driven rewrites,
// and only fall back to defining placeholder types here as a last resort.

namespace Agent.Acp.Schema;
