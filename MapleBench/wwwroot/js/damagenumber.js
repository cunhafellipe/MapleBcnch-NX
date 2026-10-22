// Damage-number glyph lookup, ported from Elem8100/MapleNecrocer
// Client/DamageNumber.cs (MIT).
//
//   C# Create():  Len = ANumber.ToString().Length
//                 Mid = (Len * 28) / 2
//                 X = AX - Mid / 2              <- centred on the mob
//   C# DoDraw():  W = 20 per glyph
//                 image = I == 0 ? LargeNumber : SmallNumber
//                 OffY  = (I % 2) == 0 ? 0 : -5  <- staggered baseline
//   C# DoMove():  Y -= 0.5f ; Counter > 50 -> Alpha -= 6 ; Alpha < 10 -> dead
//
// C# names the glyph sets from DamageNumber.Style: Style.LeftStr(5) + "1" for
// the leading digit, + "0" for the rest. With Style = "NoRed" that is
// Effect/BasicEff.img/NoRed1/<digit> and .../NoRed0/<digit>. Measured on this
// machine's Effect.nx: NoRed1/0 is 37x38 and NoRed0/0 is 31x33, so the "1" set
// is the taller one -- as the C# comment says.
//
// The geometry constants (advance, stagger, rise, fade) are duplicated in
// walkview.js beside the draw loop that uses them, and are not exported here:
// this file answers "which Image is this digit", nothing else.
//
// The server's canvas endpoint keys the FIRST path segment on an open-file id,
// not an archive name -- "No open file with id 'Effect'" if you send the name
// (measured). Ids are handed out per session by /api/files, so Effect.nx is
// resolved by file name and no id is ever assumed.

const STYLE = 'NoRed';     // C# DamageNumber.Style; LeftStr(5) == "NoRed"

const cache = { id: null, probing: false, images: new Map() };

/** Resolve the Effect archive's open-file id. Runs at most one at a time. */
async function resolveArchive(api) {
  if (cache.id) return cache.id;
  if (cache.probing) return null;
  cache.probing = true;
  try {
    const res = await api.files();                 // api.js:123
    const rows = Array.isArray(res) ? res : (res?.files ?? []);
    const hit = rows.find((f) => /Effect\.nx$/i.test(f.name ?? f.filePath ?? ''));
    cache.id = hit?.id ?? null;
  } catch {
    cache.id = null;                               // retried on a later frame
  }
  cache.probing = false;
  return cache.id;
}

/** Start loading one glyph. Fire-and-forget: a draw loop cannot await. */
function ensure(api, set, digit) {
  const key = set + digit;
  if (cache.images.has(key)) return;               // loaded, or known absent
  if (!cache.id) return;                           // id not resolved yet
  const img = new Image();
  img.onload = () => { if (img.width > 0) cache.images.set(key, img); };
  img.onerror = () => cache.images.set(key, null); // asked, no pixels
  cache.images.set(key, null);                      // in flight
  // The leading segment is the open-file id, not a folder: the path is
  // "<id>/<inside the archive>", so "f4/BasicEff.img/NoRed1/0" and
  // "f4/f1/BasicEff.img/NoRed1/0" are different requests and only the first
  // is a path in this client (measured: 200 / 958 bytes, and 404 for the other).
  img.src = api.canvasUrl(`${cache.id}/BasicEff.img/${STYLE}${set}/${digit}`);
}

const TABLE = {
  /** C#: I == 0 -> LargeNumber (NoRed1). */
  large: (d) => cache.images.get('1' + d) || null,
  /** C#: I > 0  -> SmallNumber (NoRed0). */
  small: (d) => cache.images.get('0' + d) || null,
};

/**
 * The glyph table the draw loop reads, plus a queue for whatever digits this
 * frame mentions. Synchronous on purpose.
 *
 * Returns null until the archive id resolves: the caller draws nothing that
 * frame and asks again next frame. A digit still in flight reads as null and is
 * skipped rather than drawn wrong, so the first frame to show a given digit
 * loses that digit and the next frame has it.
 *
 * @param {object} api       the api.js instance
 * @param {Array<number>} values damage values about to be drawn
 */
function glyphsFor(api, values) {
  if (!cache.id && !cache.probing) resolveArchive(api);
  if (!cache.id) return null;
  for (const v of values ?? []) {
    if (v === null || v === undefined) continue;
    const text = String(Math.max(0, Math.trunc(v)));
    for (let i = 0; i < text.length; i++) {
      ensure(api, i === 0 ? '1' : '0', text.charAt(i));
    }
  }
  return TABLE;
}

/** Forget the resolved archive and every fetched glyph. */
function reset() {
  cache.id = null;
  cache.probing = false;
  cache.images.clear();
}

export { STYLE, glyphsFor, reset };
