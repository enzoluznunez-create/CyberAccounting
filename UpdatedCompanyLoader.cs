using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class UpdatedCompanyLoader : MonoBehaviour
{
    [Header("CSV")]
    [Tooltip("File must be located under Assets/StreamingAssets")]
    public string csvFileName = "companies.csv";

    [Header("Height Scaling")]
    [Tooltip("If normalizeHeights is OFF: meters per 1 money unit (Revenue/Expenses/Tax/NetIncome)")]
    public float heightScale = 0.05f;

    [Tooltip("Normalize heights so the tallest Revenue becomes targetMaxHeight (meters)")]
    public bool normalizeHeights = true;

    [Tooltip("Used only if normalizeHeights = true")]
    public float targetMaxHeight = 3.0f;

    [Header("Base Area (EPS)")]
    [Tooltip("Base area = |EPS| * areaScale (in m^2). Square base is used.")]
    public float areaScale = 0.10f;

    [Tooltip("Minimum base side length (meters)")]
    public float minSide = 0.20f;

    [Header("Layout")]
    [Tooltip("Prefab with CompanyBlockView + child cubes: Expenses, Tax, NetIncome (+ label)")]
    public GameObject blockPrefab;

    [Tooltip("How many blocks per row in the grid")]
    public int columns = 6;

    [Tooltip("Spacing between blocks in the grid (x,z)")]
    public Vector2 spacing = new Vector2(2.0f, 2.0f);

    /// <summary>
    /// Tracks all spawned blocks by their original clean label.
    /// Used by CompanyRemovalBridge for reliable lookup and removal.
    /// Key = original company label, Value = spawned GameObject.
    /// </summary>
    public Dictionary<string, GameObject> SpawnedBlocks { get; } = new Dictionary<string, GameObject>();

    private List<CompanyRow> _rows;

    [Serializable]
    public class CompanyRow
    {
        public string Company;
        public float Revenue;
        public float Expenses;
        public float Tax;
        public float EPS;
        public string RawLine;
    }

    private void Start()
    {
        StartCoroutine(LoadAndSpawn());
    }

    private IEnumerator LoadAndSpawn()
    {
        if (blockPrefab == null)
        {
            Debug.LogError("[CompanyLoader] Missing blockPrefab reference.");
            yield break;
        }

        string path = Path.Combine(Application.streamingAssetsPath, csvFileName);
        Debug.Log("[CompanyLoader] CSV path: " + path);

        string csvText;

#if UNITY_ANDROID && !UNITY_EDITOR
        using (UnityWebRequest req = UnityWebRequest.Get(path))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[CompanyLoader] Failed to load CSV: {req.error} | {path}");
                yield break;
            }
            csvText = req.downloadHandler.text;
        }
#else
        if (!File.Exists(path))
        {
            Debug.LogError($"[CompanyLoader] CSV not found: {path}");
            yield break;
        }
        csvText = File.ReadAllText(path, Encoding.UTF8);
#endif

        _rows = ParseCsv(csvText);
        Debug.Log("[CompanyLoader] Parsed rows: " + (_rows?.Count ?? 0));

        if (_rows == null || _rows.Count == 0)
        {
            Debug.LogError("[CompanyLoader] CSV parse returned no rows.");
            yield break;
        }

        float hScale = heightScale;
        if (normalizeHeights)
        {
            float maxRev = Mathf.Max(0.0001f, _rows.Max(r => Mathf.Max(0f, r.Revenue)));
            hScale = targetMaxHeight / maxRev;
            Debug.Log($"[CompanyLoader] Height normalized: tallest revenue -> {targetMaxHeight} m (hScale={hScale})");
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            int cx = i % Mathf.Max(1, columns);
            int cz = i / Mathf.Max(1, columns);

            Vector3 pos = new Vector3(cx * spacing.x, 0f, cz * spacing.y);

            GameObject go = Instantiate(blockPrefab, pos, Quaternion.identity, transform);
            go.name = $"Block_{SanitizeName(_rows[i].Company)}";

            // Register in tracking dictionary using original clean label
            SpawnedBlocks[_rows[i].Company] = go;

            CompanyBlockView view = go.GetComponent<CompanyBlockView>();
            if (view == null)
            {
                Debug.LogError("[CompanyLoader] blockPrefab is missing CompanyBlockView.");
                yield break;
            }

            float revenue  = Mathf.Max(0f, _rows[i].Revenue);
            float expenses = Mathf.Max(0f, _rows[i].Expenses);
            float tax      = Mathf.Max(0f, _rows[i].Tax);

            float netIncome;
            float hExpenses, hTax, hNetIncome;

            if (expenses > revenue)
            {
                netIncome  = 0f;
                hNetIncome = revenue              * hScale;
                hExpenses  = (expenses - revenue) * hScale;
                hTax       = tax                  * hScale;
            }
            else
            {
                float maxTaxAllowed = Mathf.Max(0f, revenue - expenses);
                if (tax > maxTaxAllowed) tax = maxTaxAllowed;
                netIncome  = Mathf.Max(0f, revenue - expenses - tax);
                hExpenses  = expenses  * hScale;
                hTax       = tax       * hScale;
                hNetIncome = netIncome * hScale;
            }

            float baseArea = Mathf.Max(0f, Mathf.Abs(_rows[i].EPS) * areaScale);
            float side     = Mathf.Sqrt(baseArea);
            if (float.IsNaN(side) || side <= 0f) side = minSide;
            side = Mathf.Max(side, minSide);

            view.Apply(
                company:    _rows[i].Company,
                revenue:    revenue,
                expenses:   expenses,
                tax:        tax,
                eps:        _rows[i].EPS,
                baseWidth:  side,
                baseDepth:  side,
                hExpenses:  hExpenses,
                hTax:       hTax,
                hNetIncome: hNetIncome,
                rawLine:    _rows[i].RawLine
            );
        }

        Debug.Log($"[CompanyLoader] Spawned {_rows.Count} blocks from '{csvFileName}'.");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawns a single company block from data fetched via the CyberAccounting REST API.
    /// Registers the block in SpawnedBlocks for tracking.
    /// </summary>
    public void SpawnAPICompany(string label, float revenue, float expense, float tax, float eps)
    {
        if (blockPrefab == null)
        {
            Debug.LogError("[CompanyLoader] SpawnAPICompany: blockPrefab not assigned.");
            return;
        }

        int i  = transform.childCount;
        int cx = i % Mathf.Max(1, columns);
        int cz = i / Mathf.Max(1, columns);
        Vector3 pos = new Vector3(cx * spacing.x, 0f, cz * spacing.y);

        float maxVal = Mathf.Max(revenue, expense, 0.0001f);
        float hScale = targetMaxHeight / maxVal;

        float netIncome, hExpenses, hTax, hNetIncome;

        if (expense > revenue)
        {
            netIncome  = 0f;
            hNetIncome = revenue             * hScale;
            hExpenses  = (expense - revenue) * hScale;
            hTax       = tax                 * hScale;
        }
        else
        {
            float maxTaxAllowed = Mathf.Max(0f, revenue - expense);
            if (tax > maxTaxAllowed) tax = maxTaxAllowed;
            netIncome  = Mathf.Max(0f, revenue - expense - tax);
            hExpenses  = expense   * hScale;
            hTax       = tax       * hScale;
            hNetIncome = netIncome * hScale;
        }

        float baseArea = Mathf.Max(0f, Mathf.Abs(eps) * areaScale);
        float side     = Mathf.Sqrt(baseArea);
        if (float.IsNaN(side) || side <= 0f) side = minSide;
        side = Mathf.Max(side, minSide);

        GameObject go = Instantiate(blockPrefab, pos, Quaternion.identity, transform);
        go.name = "APIBlock_" + label.Replace('\n', '_').Replace(' ', '_');

        // Register in tracking dictionary using original clean label
        SpawnedBlocks[label] = go;

        CompanyBlockView view = go.GetComponent<CompanyBlockView>();
        if (view == null)
        {
            Debug.LogError("[CompanyLoader] blockPrefab is missing CompanyBlockView.");
            return;
        }

        view.Apply(
            company:    label,
            revenue:    revenue,
            expenses:   expense,
            tax:        tax,
            eps:        eps,
            baseWidth:  side,
            baseDepth:  side,
            hExpenses:  hExpenses,
            hTax:       hTax,
            hNetIncome: hNetIncome,
            rawLine:    $"API: {label}"
        );

        Debug.Log($"[CompanyLoader] Spawned API block '{label}' at {pos}.");
    }

    // ── CSV parsing helpers ───────────────────────────────────────────────────

    private List<CompanyRow> ParseCsv(string text)
    {
        var list = new List<CompanyRow>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= 1) return list;

        char delim = DetectDelimiter(lines[0]);

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = SplitCsvLine(line, delim);
            if (cols.Count < 5)
            {
                Debug.LogWarning($"[CompanyLoader] Skipping line {i + 1} (need 5 cols): {line}");
                continue;
            }

            var row = new CompanyRow
            {
                Company  = cols[0],
                Revenue  = ParseFloat(cols, 1),
                Expenses = ParseFloat(cols, 2),
                Tax      = ParseFloat(cols, 3),
                EPS      = ParseFloat(cols, 4),
                RawLine  = line
            };

            list.Add(row);
        }

        if (list.Count > 0)
        {
            var r0 = list[0];
            Debug.Log($"[CompanyLoader] First row → {r0.Company} Rev={r0.Revenue} Exp={r0.Expenses} Tax={r0.Tax} EPS={r0.EPS}");
        }

        return list;
    }

    private char DetectDelimiter(string header)
    {
        if (header.Contains("\t")) return '\t';
        if (header.Contains(";"))  return ';';
        return ',';
    }

    private float ParseFloat(List<string> cols, int idx)
    {
        if (idx >= cols.Count) return 0f;
        string s = cols[idx]?.Trim() ?? "";
        if (s.Length == 0) return 0f;
        s = s.Replace(" ", "").Replace(',', '.');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }

    private List<string> SplitCsvLine(string line, char delim)
    {
        var result   = new List<string>();
        bool inQuotes = false;
        var cur      = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '\"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                {
                    cur.Append('\"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delim && !inQuotes)
            {
                result.Add(cur.ToString().Trim());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }

        result.Add(cur.ToString().Trim());

        for (int i = 0; i < result.Count; i++)
        {
            string s = result[i];
            if (s.Length >= 2 && s[0] == '\"' && s[^1] == '\"')
                result[i] = s.Substring(1, s.Length - 2);
        }

        return result;
    }

    private string SanitizeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Unnamed";
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}