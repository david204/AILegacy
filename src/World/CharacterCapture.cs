using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AILegacy.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Tableaus;

namespace AILegacy.World
{
    /// <summary>
    /// Pictures of a character drawn by the game itself, for an image model to copy: the same renderer as the inventory
    /// screen (CharacterTableau), off screen, with the character's real body and what they wear right now. Two pictures:
    /// a face sheet (the head from the front and both three-quarters, helmet off, close up) and the whole figure. Each is
    /// cropped to the person and its colours put right (the engine's PNG has red and blue swapped). One tableau at a time,
    /// ticked on the main thread; files are read back on a worker. Kept in memory by look, so a change of clothes or face
    /// makes new ones.
    /// </summary>
    internal static class CharacterCapture
    {
        /// <summary>What a character looks like, in the codes the renderer takes.</summary>
        internal sealed class Look
        {
            public string Id, Name, Body, Equipment, FaceEquipment, Banner;
            /// <summary>What they wear on the head right now (helmet, hood, hat…), or null: the face sheet is drawn without it.</summary>
            public string Headwear;
            public bool Female;
            public int Race;
            public uint Color1, Color2;
            public string Key => Id + "|" + Body + "|" + Equipment + "|" + FaceEquipment + "|" + Female + "|" + Race + "|" + Color1 + "|" + Color2 + "|" + Banner;
        }

        internal sealed class Pictures { public byte[] Face, Figure; }

        private sealed class Job
        {
            public Look Look; public bool Face;
            public TaskCompletionSource<byte[]> Done = new TaskCompletionSource<byte[]>();
        }

        private const int FigureW = 768, FigureH = 1280, FaceSize = 1024;
        private static readonly Queue<Job> Jobs = new Queue<Job>();
        private static readonly Dictionary<string, Pictures> Cache = new Dictionary<string, Pictures>();
        private static readonly List<string> CacheOrder = new List<string>();
        private static Job _cur;
        private static CharacterTableau _t;
        private static float _age, _rest;
        private static int _frames;
        private static bool _closeUp;
        private static readonly List<string> _files = new List<string>();

        private static readonly FieldInfo FAgentLoading = typeof(CharacterTableau).GetField("_agentVisualLoadingCounter", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FMountLoading = typeof(CharacterTableau).GetField("_mountVisualLoadingCounter", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FCamPos = typeof(CharacterTableau).GetField("_camPos", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void ResetSession()
        {
            lock (Cache) { Cache.Clear(); CacheOrder.Clear(); }
            while (Jobs.Count > 0) Jobs.Dequeue().Done.TrySetResult(null);
            Abort();
        }

        // ============================================================ what they look like now

        /// <summary>
        /// The look of a hero as they are now: if they stand in the current scene, exactly what their agent wears; otherwise
        /// the civilian or the battle outfit as the scene calls for, the colours of their realm and their clan's banner.
        /// </summary>
        public static Look LookOf(Hero h)
        {
            if (h?.CharacterObject == null) return null;
            try
            {
                Equipment eq = null; string body = null; uint c1 = 0, c2 = 0; bool got = false;
                var mission = Mission.Current;
                if (mission != null)
                {
                    var a = mission.Agents.FirstOrDefault(x => x != null && x.IsActive() && x.IsHuman && (x.Character as CharacterObject)?.HeroObject == h);
                    if (a != null && a.SpawnEquipment != null)
                    {
                        eq = a.SpawnEquipment; body = a.BodyPropertiesValue.ToString(); c1 = a.ClothingColor1; c2 = a.ClothingColor2; got = true;
                    }
                }
                if (!got)
                {
                    bool civilian = false;
                    try { civilian = mission != null && mission.DoesMissionRequireCivilianEquipment; } catch { }
                    eq = civilian ? (h.CivilianEquipment ?? h.BattleEquipment) : (h.BattleEquipment ?? h.CivilianEquipment);
                    body = h.BodyProperties.ToString();
                    c1 = h.MapFaction?.Color ?? h.Clan?.Color ?? uint.MaxValue;
                    c2 = h.MapFaction?.Color2 ?? h.Clan?.Color2 ?? uint.MaxValue;
                }
                if (eq == null) return null;
                // the face sheet: no helmet, no mount, no weapons in the way of the face
                var face = eq.Clone(true);
                foreach (var slot in new[] { EquipmentIndex.Head, EquipmentIndex.Horse, EquipmentIndex.HorseHarness, EquipmentIndex.Weapon0, EquipmentIndex.Weapon1, EquipmentIndex.Weapon2, EquipmentIndex.Weapon3, EquipmentIndex.ExtraWeaponSlot })
                    face[slot] = EquipmentElement.Invalid;
                return new Look
                {
                    Id = h.StringId, Name = h.Name?.ToString() ?? h.StringId, Body = body, Equipment = eq.CalculateEquipmentCode(), FaceEquipment = face.CalculateEquipmentCode(),
                    Banner = h.ClanBanner?.Serialize() ?? "", Female = h.IsFemale, Race = h.CharacterObject.Race, Color1 = c1, Color2 = c2,
                    Headwear = eq[EquipmentIndex.Head].Item?.Name?.ToString(),
                };
            }
            catch (Exception ex) { Log.Warn($"[capture] look of {h?.Name}: {ex.Message}"); return null; }
        }

        /// <summary>The look of someone met in a scene who is not a hero (a resident), from their body there.</summary>
        public static Look LookOfAgent(Agent a, string id, string name)
        {
            try
            {
                if (a == null || !a.IsActive() || a.SpawnEquipment == null || !(a.Character is CharacterObject c)) return null;
                var eq = a.SpawnEquipment;
                var face = eq.Clone(true);
                foreach (var slot in new[] { EquipmentIndex.Head, EquipmentIndex.Horse, EquipmentIndex.HorseHarness, EquipmentIndex.Weapon0, EquipmentIndex.Weapon1, EquipmentIndex.Weapon2, EquipmentIndex.Weapon3, EquipmentIndex.ExtraWeaponSlot })
                    face[slot] = EquipmentElement.Invalid;
                return new Look
                {
                    Id = id ?? c.StringId, Name = name ?? c.Name?.ToString(), Body = a.BodyPropertiesValue.ToString(), Equipment = eq.CalculateEquipmentCode(), FaceEquipment = face.CalculateEquipmentCode(),
                    Banner = "", Female = a.IsFemale, Race = c.Race, Color1 = a.ClothingColor1, Color2 = a.ClothingColor2,
                    Headwear = eq[EquipmentIndex.Head].Item?.Name?.ToString(),
                };
            }
            catch (Exception ex) { Log.Warn($"[capture] look of {name}: {ex.Message}"); return null; }
        }

        /// <summary>Face sheet and figure of this look, from memory or drawn now. Null pictures where it could not be drawn in time.</summary>
        public static async Task<Pictures> Get(Look look, int timeoutMs = 13000)
        {
            if (look == null) return new Pictures();
            lock (Cache) { if (Cache.TryGetValue(look.Key, out var have) && have.Face != null && have.Figure != null) return have; }
            var face = new Job { Look = look, Face = true };
            var figure = new Job { Look = look, Face = false };
            MainThread.Post(() => { Jobs.Enqueue(face); Jobs.Enqueue(figure); });
            var all = Task.WhenAll(face.Done.Task, figure.Done.Task);
            await Task.WhenAny(all, Task.Delay(timeoutMs)).ConfigureAwait(false);
            var p = new Pictures
            {
                Face = face.Done.Task.Status == TaskStatus.RanToCompletion ? face.Done.Task.Result : null,
                Figure = figure.Done.Task.Status == TaskStatus.RanToCompletion ? figure.Done.Task.Result : null,
            };
            if (p.Face != null && p.Figure != null)
                lock (Cache)
                {
                    Cache[look.Key] = p; CacheOrder.Remove(look.Key); CacheOrder.Add(look.Key);
                    while (CacheOrder.Count > 40) { Cache.Remove(CacheOrder[0]); CacheOrder.RemoveAt(0); }
                }
            Log.Info($"[capture] {look.Name}: face {(p.Face != null ? p.Face.Length / 1024 + " KB" : "none")}, figure {(p.Figure != null ? p.Figure.Length / 1024 + " KB" : "none")}");
            return p;
        }

        // ============================================================ the renderer, frame by frame (main thread)

        public static void Tick(float dt)
        {
            try
            {
                if (_t == null)
                {
                    if ((_rest -= dt) > 0f || Jobs.Count == 0) return;
                    Start(Jobs.Dequeue());
                    return;
                }
                _t.OnTick(dt);
                _age += dt;
                if (_age > 10f) { Log.Warn($"[capture] {_cur?.Look?.Name}: the renderer took too long"); Fail(); return; }
                if (_files.Count == 0 && !_closeUp)
                {
                    int loading = 0;
                    try { loading = (int)(FAgentLoading?.GetValue(_t) ?? 0) + (int)(FMountLoading?.GetValue(_t) ?? 0); } catch { }
                    bool ready = (_age >= 1f && loading == 0) || _age >= 4f;
                    if (!ready) return;
                    if (_cur.Face) CloseUp();
                    _closeUp = true; _frames = 0;
                    return;
                }
                if (++_frames < 3) return;
                _frames = 0;
                var tex = _t.Texture;
                if (tex == null || !tex.IsLoaded()) return;
                string dir = TempDir();
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "cap_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".png");
                tex.SaveToFile(file, false);
                _files.Add(file);
                if (_cur.Face && _files.Count < 3)
                {
                    // the next angle: three-quarters to one side, then to the other (about 49 degrees each way)
                    _t.OnCharacterTableauMouseMove(_files.Count == 1 ? 170 : -340);
                    return;
                }
                var job = _cur; var files = _files.ToList();
                Stop();
                Task.Run(() =>
                {
                    try { job.Done.TrySetResult(Combine(files, job.Face)); }
                    catch (Exception ex) { Log.Warn("[capture] reading the pictures: " + ex.Message); job.Done.TrySetResult(null); }
                    finally { foreach (var f in files) { try { File.Delete(f); } catch { } } }
                });
            }
            catch (Exception ex) { Log.Error("[capture] renderer", ex); Fail(); }
        }

        private static void Start(Job job)
        {
            _cur = job; _age = 0f; _frames = 0; _closeUp = false; _files.Clear();
            var l = job.Look;
            var t = new CharacterTableau();
            t.SetCharStringID(l.Id);
            t.SetIsFemale(l.Female);
            t.SetRace(l.Race);
            t.SetBodyProperties(l.Body);
            t.SetEquipmentCode(job.Face ? l.FaceEquipment : l.Equipment);
            t.SetArmorColor1(l.Color1);
            t.SetArmorColor2(l.Color2);
            if (!string.IsNullOrEmpty(l.Banner)) t.SetBannerCode(l.Banner);
            t.SetStanceIndex(job.Face ? 1 : 0);      // 1 = the face brought forward, 0 = standing
            t.SetTargetSize(job.Face ? FaceSize : FigureW, job.Face ? FaceSize : FigureH);
            _t = t;
        }

        /// <summary>Brings the camera closer to the face than the game's own face view.</summary>
        private static void CloseUp()
        {
            try
            {
                if (FCamPos == null) return;
                var cam = (MatrixFrame)FCamPos.GetValue(_t);
                cam.Advance(1f);
                cam.Elevate(0.5f);
                FCamPos.SetValue(_t, cam);
            }
            catch (Exception ex) { Log.Warn("[capture] close-up: " + ex.Message); }
        }

        private static void Stop()
        {
            try { _t?.OnFinalize(); } catch { }
            _t = null; _cur = null; _files.Clear(); _rest = 0.5f;
        }

        private static void Fail()
        {
            var job = _cur;
            foreach (var f in _files) { try { File.Delete(f); } catch { } }
            Stop();
            job?.Done.TrySetResult(null);
        }

        private static void Abort() { if (_t != null) Fail(); }

        private static string TempDir() => Path.Combine(Paths.ModuleDir, "save_data", "_capture_tmp");

        // ============================================================ the pictures put right (worker thread)

        private static byte[] Combine(List<string> files, bool face)
        {
            var crops = new List<Bitmap>();
            try
            {
                foreach (var f in files)
                {
                    var raw = ReadWhenWritten(f, 2500);
                    if (raw == null) continue;
                    using (var ms = new MemoryStream(raw))
                    using (var src = new Bitmap(ms))
                    {
                        var bmp = Fix(src);
                        var box = Subject(bmp);
                        var crop = bmp.Clone(box, PixelFormat.Format32bppArgb);
                        bmp.Dispose();
                        crops.Add(crop);
                    }
                }
                if (crops.Count == 0) return null;
                Bitmap result;
                if (!face || crops.Count == 1) result = new Bitmap(crops[0]);
                else
                {
                    // the angles side by side, all the same height
                    int h = crops.Max(c => c.Height);
                    var widths = crops.Select(c => (int)Math.Round(c.Width * (h / (double)c.Height))).ToList();
                    result = new Bitmap(widths.Sum(), h, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(result))
                    {
                        g.Clear(System.Drawing.Color.FromArgb(255, 127, 127, 127));
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        int x = 0;
                        for (int i = 0; i < crops.Count; i++) { g.DrawImage(crops[i], new Rectangle(x, 0, widths[i], h)); x += widths[i]; }
                    }
                }
                using (result)
                {
                    var fitted = Fit(result, 1536);
                    using (var ms = new MemoryStream())
                    {
                        fitted.Save(ms, ImageFormat.Png);
                        if (!ReferenceEquals(fitted, result)) fitted.Dispose();
                        return ms.ToArray();
                    }
                }
            }
            finally { foreach (var c in crops) c.Dispose(); }
        }

        private static byte[] ReadWhenWritten(string file, int maxMs)
        {
            long last = -1;
            for (int waited = 0; waited <= maxMs; waited += 50)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        long len = new FileInfo(file).Length;
                        if (len > 0 && len == last) return File.ReadAllBytes(file);
                        last = len;
                    }
                }
                catch { }
                System.Threading.Thread.Sleep(50);
            }
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }

        /// <summary>A 32-bit copy with red and blue put back where they belong.</summary>
        private static Bitmap Fix(Bitmap src)
        {
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int n = Math.Abs(data.Stride) * bmp.Height;
                var px = new byte[n];
                Marshal.Copy(data.Scan0, px, 0, n);
                for (int i = 0; i + 3 < n; i += 4) { byte b = px[i]; px[i] = px[i + 2]; px[i + 2] = b; }
                Marshal.Copy(px, 0, data.Scan0, n);
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        /// <summary>The box around the person: every pixel that differs from the corner (the background), plus 3%.</summary>
        private static Rectangle Subject(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
            try
            {
                int stride = Math.Abs(data.Stride), n = stride * bmp.Height;
                var px = new byte[n];
                Marshal.Copy(data.Scan0, px, 0, n);
                int b0 = px[0], g0 = px[1], r0 = px[2], a0 = px[3];
                for (int y = 0; y < bmp.Height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int i = row + x * 4;
                        int d = Math.Abs(px[i] - b0) + Math.Abs(px[i + 1] - g0) + Math.Abs(px[i + 2] - r0) + Math.Abs(px[i + 3] - a0);
                        if (d <= 30) continue;
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            if (maxX < minX || maxY < minY) return new Rectangle(0, 0, bmp.Width, bmp.Height);
            int padX = (int)((maxX - minX) * 0.03f) + 1, padY = (int)((maxY - minY) * 0.03f) + 1;
            int x0 = Math.Max(0, minX - padX), y0 = Math.Max(0, minY - padY);
            int x1 = Math.Min(bmp.Width - 1, maxX + padX), y1 = Math.Min(bmp.Height - 1, maxY + padY);
            return new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        private static Bitmap Fit(Bitmap bmp, int max)
        {
            int longest = Math.Max(bmp.Width, bmp.Height);
            if (longest <= max) return bmp;
            float k = max / (float)longest;
            var r = new Bitmap(Math.Max(1, (int)(bmp.Width * k)), Math.Max(1, (int)(bmp.Height * k)), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(r)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(bmp, new Rectangle(0, 0, r.Width, r.Height)); }
            return r;
        }

        /// <summary>A reference ready to send: flattened on grey, as JPEG.</summary>
        internal static byte[] Jpeg(byte[] png, long quality = 95)
        {
            try
            {
                using (var ms = new MemoryStream(png))
                using (var src = new Bitmap(ms))
                using (var flat = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(flat)) { g.Clear(System.Drawing.Color.FromArgb(255, 127, 127, 127)); g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height)); }
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var pars = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality) } };
                    using (var outMs = new MemoryStream()) { flat.Save(outMs, codec, pars); return outMs.ToArray(); }
                }
            }
            catch { return png; }
        }
    }
}
