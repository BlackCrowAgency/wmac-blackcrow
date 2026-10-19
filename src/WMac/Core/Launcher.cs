using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace WMac
{
    /// <summary>Grupo VIRTUAL de iconos: solo existe en los ajustes de WMac, nunca en el disco.</summary>
    [DataContract]
    public sealed class LauncherGroup
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public List<string> Items = new List<string>();
        /// <summary>Categoría a la que se añaden automáticamente los elementos nuevos (null = grupo del usuario).</summary>
        [DataMember] public string Auto;
    }

    [DataContract]
    public sealed class LauncherLayout
    {
        /// <summary>Orden del nivel principal: claves de elementos o "g:" + id de grupo.</summary>
        [DataMember] public List<string> Order = new List<string>();
        [DataMember] public List<LauncherGroup> Groups = new List<LauncherGroup>();
        [DataMember] public bool Initialized;

        public void Fix()
        {
            Order = Order ?? new List<string>();
            Groups = Groups ?? new List<LauncherGroup>();
            foreach (var g in Groups)
            {
                g.Items = g.Items ?? new List<string>();
                g.Id = string.IsNullOrEmpty(g.Id) ? Guid.NewGuid().ToString("N").Substring(0, 8) : g.Id;
                g.Name = string.IsNullOrWhiteSpace(g.Name) ? "Grupo" : g.Name;
            }
        }
    }

    internal sealed class LauncherEntry
    {
        public readonly string Key;
        public readonly DesktopItem Item;
        public readonly LauncherGroup Group;

        public LauncherEntry(DesktopItem item) { Item = item; Key = item.Key; }
        public LauncherEntry(LauncherGroup group) { Group = group; Key = Launcher.GroupPrefix + group.Id; }

        public bool IsGroup => Group != null;
        public string Name => IsGroup ? Group.Name : Item.Name;
    }

    /// <summary>
    /// Organización del lanzador: orden, páginas y grupos virtuales. Todas las operaciones cambian solo
    /// la disposición guardada en settings.json; los archivos de Windows no se tocan.
    /// </summary>
    internal sealed class Launcher
    {
        public const string GroupPrefix = "g:";

        readonly DesktopModel _model;
        Dictionary<string, DesktopItem> _items = new Dictionary<string, DesktopItem>();

        public List<LauncherEntry> Top { get; private set; } = new List<LauncherEntry>();
        public event Action Changed;

        static LauncherLayout L => SettingsStore.Current.Launcher;
        // Las claves de elementos son rutas (contienen '\'): así "g:\..." (unidad G:) nunca se confunde con un grupo.
        public static bool IsGroupKey(string k) => k != null && k.StartsWith(GroupPrefix, StringComparison.Ordinal) && k.IndexOf('\\') < 0;
        static readonly IComparer<string> Logical = Comparer<string>.Create((a, b) => Native.StrCmpLogicalW(a ?? "", b ?? ""));

        public Launcher(DesktopModel model)
        {
            _model = model;
            model.ItemsChanged += Sync;
        }

        public DesktopItem Find(string key) => key != null && _items.TryGetValue(key, out var it) ? it : null;
        public LauncherGroup GroupById(string id) => L.Groups.FirstOrDefault(g => g.Id == id);
        public LauncherGroup GroupOfKey(string entryKey) => IsGroupKey(entryKey) ? GroupById(entryKey.Substring(GroupPrefix.Length)) : null;
        public LauncherGroup GroupContaining(string itemKey) => L.Groups.FirstOrDefault(g => g.Items.Contains(itemKey));
        public List<DesktopItem> ItemsOf(LauncherGroup g) => g == null ? new List<DesktopItem>() : g.Items.Select(Find).Where(i => i != null).ToList();
        public IReadOnlyList<DesktopItem> AllItems => _model.Items;

        // ---------- sincronización con los elementos reales ----------

        public void Sync()
        {
            _items = _model.Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
            if (_model.Items.Count == 0) { Rebuild(); return; }   // nunca se borra la disposición por una lectura vacía
            if (!L.Initialized) { BuildDefault(); return; }

            bool changed = false;
            var placed = new HashSet<string>(L.Order.Where(k => !IsGroupKey(k)), StringComparer.Ordinal);
            foreach (var g in L.Groups) foreach (var k in g.Items) placed.Add(k);
            foreach (var it in _model.Items.Where(i => !placed.Contains(i.Key)).OrderBy(i => i.Name, Logical))
            {
                Place(it);
                changed = true;
            }
            changed |= Normalize();
            if (changed) Save();
            Rebuild();
        }

        void Place(DesktopItem it)
        {
            var auto = L.Groups.FirstOrDefault(g => g.Auto == it.Category.ToString());
            if (auto != null)
            {
                int at = auto.Items.FindIndex(k => Logical.Compare(Find(k)?.Name ?? k, it.Name) > 0);
                if (at < 0) auto.Items.Add(it.Key); else auto.Items.Insert(at, it.Key);
                return;
            }
            int idx = -1;
            for (int i = L.Order.Count - 1; i >= 0; i--)
                if (!IsGroupKey(L.Order[i]) && Find(L.Order[i])?.Category == it.Category) { idx = i; break; }
            if (idx >= 0) L.Order.Insert(idx + 1, it.Key); else L.Order.Add(it.Key);
        }

        /// <summary>Quita duplicados y elementos que ya no existen; ajusta grupos vacíos o de un solo elemento.</summary>
        bool Normalize()
        {
            bool changed = false;
            var dirExists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool Gone(string k)
            {
                if (_items.ContainsKey(k)) return false;
                if (k.StartsWith("::")) return true;
                // Rutas de red: no se comprueban (podrían bloquear la interfaz si no hay conexión).
                if (k.StartsWith(@"\\")) return false;
                try
                {
                    string dir = Path.GetDirectoryName(k);
                    if (string.IsNullOrEmpty(dir)) return false;
                    if (!dirExists.TryGetValue(dir, out bool exists)) dirExists[dir] = exists = Directory.Exists(dir);
                    return exists && !File.Exists(k) && !Directory.Exists(k);
                }
                catch { return false; }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < L.Order.Count; i++)
            {
                var k = L.Order[i];
                if (!seen.Add(k) || (!IsGroupKey(k) && Gone(k))) { L.Order.RemoveAt(i--); changed = true; }
            }
            foreach (var g in L.Groups)
                for (int i = 0; i < g.Items.Count; i++)
                {
                    var k = g.Items[i];
                    if (!seen.Add(k) || Gone(k)) { g.Items.RemoveAt(i--); changed = true; }
                }

            changed |= L.Groups.RemoveAll(g => g.Items.Count == 0) > 0;
            var ids = new HashSet<string>(L.Groups.Select(g => GroupPrefix + g.Id));
            changed |= L.Order.RemoveAll(k => IsGroupKey(k) && !ids.Contains(k)) > 0;
            foreach (var g in L.Groups)
                if (!L.Order.Contains(GroupPrefix + g.Id)) { L.Order.Add(GroupPrefix + g.Id); changed = true; }
            foreach (var g in L.Groups.Where(g => g.Auto == null && g.Items.Count == 1).ToList())
            {
                int i = L.Order.IndexOf(GroupPrefix + g.Id);
                L.Order[i] = g.Items[0];
                L.Groups.Remove(g);
                changed = true;
            }
            return changed;
        }

        void Rebuild()
        {
            var top = new List<LauncherEntry>();
            foreach (var k in L.Order)
            {
                if (IsGroupKey(k))
                {
                    var g = GroupOfKey(k);
                    if (g != null && g.Items.Any(i => _items.ContainsKey(i))) top.Add(new LauncherEntry(g));
                }
                else if (_items.TryGetValue(k, out var it)) top.Add(new LauncherEntry(it));
            }
            Top = top;
            Changed?.Invoke();
        }

        static void Save() => SettingsStore.SaveSoon();

        static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>Organización automática: carpetas y documentos en grupos; el resto ordenado por categoría.</summary>
        public void BuildDefault()
        {
            L.Order.Clear();
            L.Groups.Clear();
            var items = _model.Items;
            foreach (var cat in new[] { Category.Carpetas, Category.Documentos })
            {
                var list = items.Where(i => i.Category == cat).OrderBy(i => i.Name, Logical).Select(i => i.Key).ToList();
                if (list.Count < 2) continue;
                var g = new LauncherGroup { Id = NewId(), Name = cat.ToString(), Auto = cat.ToString(), Items = list };
                L.Groups.Add(g);
                L.Order.Add(GroupPrefix + g.Id);
            }
            var grouped = new HashSet<string>(L.Groups.SelectMany(g => g.Items));
            foreach (var it in items.Where(i => !grouped.Contains(i.Key)).OrderBy(i => Categorizer.Rank(i.Category)).ThenBy(i => i.Name, Logical))
                L.Order.Add(it.Key);
            L.Initialized = true;
            Save();
            Rebuild();
        }

        // ---------- operaciones del usuario (todas virtuales) ----------

        int OrderIndexForDisplay(int displayIndex, string excluding)
        {
            var keys = Top.Select(e => e.Key).Where(k => k != excluding).ToList();
            if (displayIndex < 0) displayIndex = 0;
            if (displayIndex >= keys.Count) return L.Order.Count;
            int oi = L.Order.IndexOf(keys[displayIndex]);
            return oi < 0 ? L.Order.Count : oi;
        }

        void RemoveEverywhere(string key)
        {
            L.Order.Remove(key);
            foreach (var g in L.Groups) g.Items.Remove(key);
        }

        public void MoveTop(string key, int displayIndex)
        {
            if (key == null) return;
            bool wasTop = L.Order.Contains(key);
            if (!wasTop && !IsGroupKey(key)) foreach (var g in L.Groups) g.Items.Remove(key);
            L.Order.Remove(key);
            int oi = OrderIndexForDisplay(displayIndex, key);
            L.Order.Insert(Math.Min(oi, L.Order.Count), key);
            Normalize();
            Save();
            Rebuild();
        }

        /// <summary>Soltar un icono sobre otro: crea un grupo (o lo añade al grupo existente).</summary>
        public LauncherGroup Merge(string draggedKey, string targetKey)
        {
            if (draggedKey == null || targetKey == null || draggedKey == targetKey || IsGroupKey(draggedKey)) return null;
            LauncherGroup g;
            if (IsGroupKey(targetKey))
            {
                g = GroupOfKey(targetKey);
                if (g == null) return null;
                RemoveEverywhere(draggedKey);
                g.Items.Add(draggedKey);
            }
            else
            {
                var a = Find(targetKey);
                var b = Find(draggedKey);
                if (a == null || b == null) return null;
                RemoveEverywhere(draggedKey);
                int at = L.Order.IndexOf(targetKey);
                L.Order.Remove(targetKey);
                foreach (var gg in L.Groups) gg.Items.Remove(targetKey);
                string name = a.Category == b.Category ? a.Category.ToString() : "Grupo nuevo";
                g = new LauncherGroup { Id = NewId(), Name = name, Items = new List<string> { targetKey, draggedKey } };
                L.Groups.Add(g);
                L.Order.Insert(at < 0 ? L.Order.Count : Math.Min(at, L.Order.Count), GroupPrefix + g.Id);
            }
            Normalize();
            Save();
            Rebuild();
            return g;
        }

        public LauncherGroup GroupSelection(IList<string> itemKeys, int displayIndex)
        {
            var keys = itemKeys.Where(k => !IsGroupKey(k) && Find(k) != null).Distinct().ToList();
            if (keys.Count < 2) return null;
            int oi = OrderIndexForDisplay(displayIndex, null);
            // El ancla es el primer elemento del orden a partir de ahí que NO forma parte de la selección.
            var set = new HashSet<string>(keys);
            string anchor = L.Order.Skip(oi).FirstOrDefault(k => !set.Contains(k));
            foreach (var k in keys) RemoveEverywhere(k);
            var g = new LauncherGroup { Id = NewId(), Name = "Grupo nuevo", Items = keys };
            L.Groups.Add(g);
            int at = anchor != null ? L.Order.IndexOf(anchor) : -1;
            L.Order.Insert(at < 0 ? L.Order.Count : at, GroupPrefix + g.Id);
            Normalize();
            Save();
            Rebuild();
            return g;
        }

        public void RemoveFromGroup(string itemKey, int displayIndex)
        {
            var g = GroupContaining(itemKey);
            if (g == null) return;
            g.Items.Remove(itemKey);
            int oi = OrderIndexForDisplay(displayIndex, null);
            L.Order.Insert(Math.Min(oi, L.Order.Count), itemKey);
            Normalize();
            Save();
            Rebuild();
        }

        public void MoveInGroup(LauncherGroup g, string itemKey, int index)
        {
            if (g == null || !g.Items.Remove(itemKey)) return;
            var present = g.Items.Where(k => Find(k) != null).ToList();
            int at = index >= present.Count ? g.Items.Count : g.Items.IndexOf(present[Math.Max(0, index)]);
            g.Items.Insert(at < 0 ? g.Items.Count : at, itemKey);
            Save();
            Rebuild();
        }

        public void Ungroup(LauncherGroup g)
        {
            if (g == null) return;
            int i = L.Order.IndexOf(GroupPrefix + g.Id);
            if (i < 0) i = L.Order.Count; else L.Order.RemoveAt(i);
            L.Order.InsertRange(Math.Min(i, L.Order.Count), g.Items);
            L.Groups.Remove(g);
            Normalize();
            Save();
            Rebuild();
        }

        public void RenameGroup(LauncherGroup g, string name)
        {
            name = (name ?? "").Trim();
            if (g == null || name.Length == 0 || name == g.Name) return;
            g.Name = name.Length > 40 ? name.Substring(0, 40) : name;
            Save();
            Rebuild();
        }

        public void SortTop(string mode)
        {
            var entries = Top.ToList();
            IEnumerable<LauncherEntry> sorted;
            switch (mode)
            {
                case "name":
                    sorted = entries.OrderBy(e => e.IsGroup ? 0 : 1).ThenBy(e => e.Name, Logical);
                    break;
                case "date":
                    sorted = entries.OrderBy(e => e.IsGroup ? 0 : 1).ThenByDescending(e => e.Item?.Modified ?? DateTime.MaxValue);
                    break;
                default:
                    sorted = entries.OrderBy(e => e.IsGroup ? 0 : 1)
                                    .ThenBy(e => e.IsGroup ? 0 : Categorizer.Rank(e.Item.Category))
                                    .ThenBy(e => e.Name, Logical);
                    break;
            }
            var keys = sorted.Select(e => e.Key).ToList();
            var hidden = L.Order.Where(k => !keys.Contains(k)).ToList();   // elementos ocultos o no disponibles: se conservan al final
            L.Order.Clear();
            L.Order.AddRange(keys);
            L.Order.AddRange(hidden);
            Save();
            Rebuild();
        }
    }
}
