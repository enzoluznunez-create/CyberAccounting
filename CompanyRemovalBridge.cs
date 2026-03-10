using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Convai.Scripts.Runtime.Core;
using Convai.Scripts.Runtime.UI;
using UnityEngine;

/// <summary>
/// Listens to NPC responses for [REMOVE] tags and handles removing company
/// blocks from the scene. Uses CompanyLoader.SpawnedBlocks for reliable
/// label-based lookup. Works independently of UpdatedConvaiAPIBridge.
///
/// Tag format:
///   [REMOVE company="Google" action="remove"]
///   [REMOVE company="MISSING" action="clearall"]
/// </summary>
public class CompanyRemovalBridge : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    #region Inspector

    [Header("References")]
    [Tooltip("The UpdatedCompanyLoader that owns and tracks all spawned blocks.")]
    public UpdatedCompanyLoader updatedCompanyLoader;

    [Header("Behaviour")]
    [Tooltip("How closely a name must match a tracked label (0-1). Lower = more forgiving.")]
    [Range(0f, 1f)]
    public float matchThreshold = 0.6f;

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Cached references

    ConvaiNPC _npc;

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Parsed remove structure

    class ParsedRemove
    {
        public string Company;
        public string Action;
        public bool HasCompany => !string.IsNullOrEmpty(Company) && Company != "MISSING";
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Lifecycle

    void Start()
    {
        _npc = GetComponent<ConvaiNPC>();
        if (_npc == null)
            Debug.LogWarning("[RemovalBridge] No ConvaiNPC found in scene.");

        if (updatedCompanyLoader == null)
            Debug.LogWarning("[RemovalBridge] updatedCompanyLoader not assigned in Inspector.");
    }

    void OnEnable()
    {
        ConvaiChatUIHandler.OnNPCResponseUpdated += OnNPCResponseReceived;
    }

    void OnDisable()
    {
        ConvaiChatUIHandler.OnNPCResponseUpdated -= OnNPCResponseReceived;
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region NPC response handler

    void OnNPCResponseReceived(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return;

        ParsedRemove cmd = ParseRemoveTag(response);
        if (cmd == null) return;

        Debug.Log($"[RemovalBridge] Command received — company=\"{cmd.Company}\" action=\"{cmd.Action}\"");
        StartCoroutine(HandleRemove(cmd));
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Remove tag parser

    ParsedRemove ParseRemoveTag(string response)
    {
        var m = Regex.Match(response,
            @"\[REMOVE\s+company=""([^""]*)""\s+action=""([^""]*)""\]",
            RegexOptions.IgnoreCase);

        if (!m.Success) return null;

        string action = m.Groups[2].Value.Trim().ToLower();

        var validActions = new HashSet<string> { "remove", "clearall" };
        if (!validActions.Contains(action))
        {
            Debug.LogWarning($"[RemovalBridge] Unknown action \"{action}\" in REMOVE tag — ignoring.");
            return null;
        }

        return new ParsedRemove
        {
            Company = m.Groups[1].Value.Trim(),
            Action  = action
        };
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Command handler

    IEnumerator HandleRemove(ParsedRemove cmd)
    {
        switch (cmd.Action)
        {
            case "remove":
                yield return HandleRemoveSingle(cmd);
                break;

            case "clearall":
                yield return HandleClearAll();
                break;
        }
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Remove single

    IEnumerator HandleRemoveSingle(ParsedRemove cmd)
    {
        if (!cmd.HasCompany)
        {
            TriggerNPC("Which company would you like to remove?");
            yield break;
        }

        if (updatedCompanyLoader.SpawnedBlocks.Count == 0)
        {
            TriggerNPC("Your dashboard is already empty.");
            yield break;
        }

        // Find the best matching label in the tracking dictionary
        string matchedLabel = FindBestMatchLabel(cmd.Company);

        if (matchedLabel == null)
        {
            string available = BuildBlockList();
            TriggerNPC($"I couldn't find {cmd.Company} in your dashboard. " +
                       $"Currently showing: {available}. Which one would you like to remove?");
            yield break;
        }

        // Destroy the block and remove from dictionary
        GameObject block = updatedCompanyLoader.SpawnedBlocks[matchedLabel];
        updatedCompanyLoader.SpawnedBlocks.Remove(matchedLabel);
        Destroy(block);

        // Wait one frame for Destroy to complete before repositioning
        yield return null;

        RepositionBlocks();

        Debug.Log($"[RemovalBridge] Removed \"{matchedLabel}\".");
        TriggerNPC($"Removed {matchedLabel} from your dashboard.");
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Clear all

    IEnumerator HandleClearAll()
    {
        if (updatedCompanyLoader.SpawnedBlocks.Count == 0)
        {
            TriggerNPC("Your dashboard is already empty.");
            yield break;
        }

        int count = updatedCompanyLoader.SpawnedBlocks.Count;

        foreach (GameObject block in updatedCompanyLoader.SpawnedBlocks.Values)
        {
            if (block != null)
                Destroy(block);
        }

        updatedCompanyLoader.SpawnedBlocks.Clear();

        yield return null;

        Debug.Log($"[RemovalBridge] Cleared {count} blocks.");
        TriggerNPC($"Cleared all {count} companies from your dashboard.");
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Matching and repositioning helpers

    /// <summary>
    /// Finds the dictionary key whose label best matches the given company name.
    /// Tries exact match first, then fuzzy. Returns null if nothing scores
    /// above matchThreshold.
    /// </summary>
    string FindBestMatchLabel(string companyName)
    {
        // Exact match first — case insensitive
        foreach (string key in updatedCompanyLoader.SpawnedBlocks.Keys)
        {
            if (string.Equals(key, companyName, System.StringComparison.OrdinalIgnoreCase))
                return key;
        }

        // Fuzzy fallback against clean original labels
        string needle    = Normalize(companyName);
        string bestLabel = null;
        float  bestScore = 0f;

        foreach (var kvp in updatedCompanyLoader.SpawnedBlocks)
        {
            if (kvp.Value == null) continue;

            float score = FuzzyScore(needle, Normalize(kvp.Key));
            Debug.Log($"[RemovalBridge] Fuzzy score \"{companyName}\" vs \"{kvp.Key}\": {score:F2}");

            if (score > bestScore)
            {
                bestScore = score;
                bestLabel = kvp.Key;
            }
        }

        return bestScore >= matchThreshold ? bestLabel : null;
    }

    /// <summary>
    /// Repositions all remaining tracked blocks to fill gaps in the grid.
    /// Reads columns and spacing directly from CompanyLoader.
    /// </summary>
    void RepositionBlocks()
    {
        int     cols    = Mathf.Max(1, updatedCompanyLoader.columns);
        Vector2 spacing = updatedCompanyLoader.spacing;

        // Only reposition blocks that are still alive
        var alive = updatedCompanyLoader.SpawnedBlocks.Values
            .Where(b => b != null)
            .ToList();

        for (int i = 0; i < alive.Count; i++)
        {
            int     cx  = i % cols;
            int     cz  = i / cols;
            Vector3 pos = new Vector3(cx * spacing.x, 0f, cz * spacing.y);
            alive[i].transform.position = pos;
        }

        Debug.Log($"[RemovalBridge] Repositioned {alive.Count} remaining blocks.");
    }

    /// <summary>
    /// Builds a readable list of currently tracked company labels for the NPC.
    /// </summary>
    string BuildBlockList()
    {
        var names = updatedCompanyLoader.SpawnedBlocks
            .Where(kvp => kvp.Value != null)
            .Select(kvp => kvp.Key)
            .ToList();

        if (names.Count == 0) return "nothing";
        if (names.Count == 1) return names[0];

        return string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
    }

    /// <summary>
    /// Normalises a string for fuzzy matching — lowercase, alphanumeric only.
    /// </summary>
    string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return Regex.Replace(s.ToLower(), @"[^a-z0-9]", "");
    }

    /// <summary>
    /// Returns a 0-1 similarity score between two normalised strings.
    /// Exact = 1.0, contains = 0.9, character overlap ratio otherwise.
    /// </summary>
    float FuzzyScore(string a, string b)
    {
        if (a == b)                                    return 1f;
        if (string.IsNullOrEmpty(a) ||
            string.IsNullOrEmpty(b))                   return 0f;
        if (b.Contains(a) || a.Contains(b))            return 0.9f;

        int          matches = 0;
        List<char>   bChars  = new List<char>(b);

        foreach (char c in a)
            if (bChars.Remove(c)) matches++;

        return (float)matches / Mathf.Max(a.Length, b.Length);
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region NPC trigger

    void TriggerNPC(string message)
    {
        if (_npc == null)
        {
            Debug.LogWarning("[RemovalBridge] No ConvaiNPC — cannot trigger speech.");
            return;
        }

        Debug.Log($"[RemovalBridge] TriggerSpeech → \"{message}\"");
        _npc.TriggerSpeech(message);
    }

    #endregion
}