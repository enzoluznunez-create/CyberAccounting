using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Convai.Scripts.Runtime.Core;
using Convai.Scripts.Runtime.UI;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Multi-turn conversation bridge between the Convai NPC and the CyberAccounting API.
///
/// Relies on the Convai LLM to parse natural language and emit a structured
/// [QUERY] tag in every response. C# handles only state management and API calls.
///
/// Conversation flow:
///   1. User speaks → Convai LLM emits [QUERY] tag → OnNPCResponseReceived fires
///   2. HandleQuery routes by action → LookupCIK or ResolveAndFetch
///   3. If data found → CompanyLoader.SpawnAPICompany → 3D block spawned
/// </summary>
public class UpdatedConvaiAPIBridge : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    #region Inspector

    [Header("References")]
    [Tooltip("CompanyLoader that owns the block grid in the scene.")]
    public CompanyLoader companyLoader;

    [Header("API")]
    [Tooltip("Root URL of the CyberAccounting API (no trailing slash).")]
    public string apiRoot = "https://cyberacc.discovery.cs.vt.edu";

    [Header("Behaviour")]
    [Tooltip("Clear FP/FY state after a successful visualisation.")]
    public bool clearAfterFetch = true;

    [Tooltip("Seconds before a waiting state times out and resets.")]
    public float stateTimeoutSeconds = 30f;

    [Tooltip("Name of the Convai SDK event that fires when the NPC response is ready.")]
    public string npcResponseEventName = "OnNPCResponseUpdated";

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region State machine

    enum State
    {
        Idle,             // waiting for any utterance
        WaitingSelection, // multiple CIK results — waiting for user to pick one
        WaitingFiscalInfo // have a CIK — waiting for FP and/or FY
    }

    State          _state = State.Idle;
    List<string[]> _candidates;   // [cik, name] pairs when multiple results
    string         _selectedCik;
    string         _selectedName;
    string         _pendingFp;
    string         _pendingFy;
    float          _stateEnteredTime;
    bool           _fetchInProgress;
    bool           _queryProcessing; // prevents overlapping query handling

    void SetState(State newState)
    {
        _state            = newState;
        _stateEnteredTime = Time.realtimeSinceStartup;
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Parsed query structure

    class ParsedQuery
    {
        public string Company;
        public string FP;
        public string FY;
        public string Action;
        public int    Pick = -1; // 1-based index for "select" action, -1 if not set

        public bool HasCompany => !string.IsNullOrEmpty(Company) && Company != "MISSING";
        public bool HasFP      => !string.IsNullOrEmpty(FP)      && FP      != "MISSING";
        public bool HasFY      => !string.IsNullOrEmpty(FY)      && FY      != "MISSING";
        public bool HasPick    => Pick > 0;
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Cached references

    ConvaiNPC _npc;

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Lifecycle

    void Start()
    {
        _npc = GetComponent<ConvaiNPC>();
        if (_npc == null)
            Debug.LogWarning("[APIBridge] No ConvaiNPC found in scene.");
    }

    void OnEnable()
    {
        SubscribeToNPCEvent();
    }

    void OnDisable()
    {
        UnsubscribeFromNPCEvent();
    }

    void Update()
    {
        if (_state == State.Idle) return;

        float elapsed = Time.realtimeSinceStartup - _stateEnteredTime;
        if (elapsed < stateTimeoutSeconds) return;

        Debug.Log($"[APIBridge] State {_state} timed out after {stateTimeoutSeconds}s, resetting.");

        string message = _state == State.WaitingSelection
            ? "I didn't hear a selection. Please say a company name to start over."
            : "I didn't hear a fiscal period or year. Please start over with a company name.";

        ResetState();
        TriggerNPC(message);
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Event subscription helpers

    void SubscribeToNPCEvent()
    {
        if (string.IsNullOrWhiteSpace(npcResponseEventName))
            return;

        var evt = typeof(ConvaiChatUIHandler)
            .GetEvent(npcResponseEventName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        if (evt != null)
        {
            evt.AddEventHandler(null, (Action<string>)OnNPCResponseReceived);
        }
        else
        {
            Debug.LogWarning($"[APIBridge] Event '{npcResponseEventName}' not found on ConvaiChatUIHandler.");
        }
    }

    void UnsubscribeFromNPCEvent()
    {
        if (string.IsNullOrWhiteSpace(npcResponseEventName))
            return;

        var evt = typeof(ConvaiChatUIHandler)
            .GetEvent(npcResponseEventName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        if (evt != null)
        {
            evt.RemoveEventHandler(null, (Action<string>)OnNPCResponseReceived);
        }
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region NPC response handler

    void OnNPCResponseReceived(string response)
    {
        
        if (string.IsNullOrWhiteSpace(response)) return;

        // Block new query processing while a fetch or query is already in progress
        if (_fetchInProgress || _queryProcessing)
        {
            Debug.Log("[APIBridge] Query or fetch in progress, ignoring new response.");
            return;
        }

        ParsedQuery query = ParseQueryTag(response);
        if (query == null)
        {
            Debug.Log("[APIBridge] No QUERY tag found in NPC response — general conversation.");
            return;
        }

        Debug.Log($"[APIBridge] Query parsed — company=\"{query.Company}\" fp=\"{query.FP}\" fy=\"{query.FY}\" action=\"{query.Action}\" pick={query.Pick}");
        StartCoroutine(HandleQuery(query));
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Query tag parser

    /// <summary>
    /// Parses the [QUERY] tag emitted by the Convai LLM.
    /// Expected format:
    ///   [QUERY company="..." fp="..." fy="..." action="..."]
    ///   [QUERY company="..." fp="..." fy="..." action="select" pick="2"]
    /// Returns null if no valid tag is found.
    /// </summary>
    ParsedQuery ParseQueryTag(string response)
    {
        var m = Regex.Match(response,
            @"\[QUERY\s+company=""([^""]*)""\s+fp=""([^""]*)""\s+fy=""([^""]*)""\s+action=""([^""]*)""(?:\s+pick=""(\d+)"")?\]",
            RegexOptions.IgnoreCase);

        if (!m.Success) return null;

        string action = m.Groups[4].Value.Trim().ToLower();

        // Validate action is a known value
        var validActions = new HashSet<string> { "fetch", "search", "waiting", "select", "cancel", "unclear" };
        if (!validActions.Contains(action))
        {
            Debug.LogWarning($"[APIBridge] Unknown action \"{action}\" in QUERY tag — ignoring.");
            return null;
        }

        return new ParsedQuery
        {
            Company = m.Groups[1].Value.Trim(),
            FP      = m.Groups[2].Value.Trim().ToUpper(),
            FY      = m.Groups[3].Value.Trim(),
            Action  = action,
            Pick    = m.Groups[5].Success
                        ? int.Parse(m.Groups[5].Value)
                        : -1
        };
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Query handler

    IEnumerator HandleQuery(ParsedQuery query)
    {
        _queryProcessing = true;

        try
        {
            switch (query.Action)
            {
                // ── Cancel ───────────────────────────────────────────────────
                case "cancel":
                    ResetState();
                    yield break;

                // ── Unclear intent ───────────────────────────────────────────
                case "unclear":
                    Debug.Log("[APIBridge] Unclear intent — waiting for more input.");
                    yield break;

                // ── Selection from candidate list ────────────────────────────
                case "select":
                    yield return HandleSelection(query);
                    yield break;

                // ── All data-bearing actions ─────────────────────────────────
                case "fetch":
                case "search":
                case "waiting":
                    // Store whatever the LLM extracted
                    if (query.HasCompany) _selectedName = query.Company;
                    if (query.HasFP)      _pendingFp    = query.FP;
                    if (query.HasFY)      _pendingFy    = query.FY;

                    if (query.HasCompany)
                    {
                        // Company name changed or CIK not yet resolved — look it up
                        bool companyChanged = !string.IsNullOrEmpty(_selectedName) &&
                                              _selectedName != query.Company         &&
                                              !string.IsNullOrEmpty(_selectedCik);

                        if (string.IsNullOrEmpty(_selectedCik) || companyChanged)
                        {
                            // Clear stale CIK and fiscal info when switching companies
                            if (companyChanged)
                            {
                                Debug.Log($"[APIBridge] Company changed from \"{_selectedName}\" to \"{query.Company}\", clearing stale CIK.");
                                _selectedCik = null;
                                _pendingFp   = query.HasFP ? query.FP : null;
                                _pendingFy   = query.HasFY ? query.FY : null;
                            }

                            yield return LookupCIK(query.Company);
                            yield break;
                        }
                    }

                    // CIK already resolved — go straight to fetch decision
                    if (!string.IsNullOrEmpty(_selectedCik))
                    {
                        yield return ResolveAndFetch();
                        yield break;
                    }

                    // No company and no CIK — ask for company name
                    TriggerNPC("I need a company name to get started. Which company are you interested in?");
                    break;
            }
        }
        finally
        {
            _queryProcessing = false;
        }
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Selection handler

    IEnumerator HandleSelection(ParsedQuery query)
    {
        if (_candidates == null || _candidates.Count == 0)
        {
            Debug.LogWarning("[APIBridge] HandleSelection called with no candidates.");
            TriggerNPC("Something went wrong with the company list. Please say a company name to start over.");
            ResetState();
            yield break;
        }

        if (!query.HasPick)
        {
            TriggerNPC("I didn't catch which one you wanted. Please say the number of the company.");
            yield break;
        }

        int idx = query.Pick - 1; // convert 1-based to 0-based

        if (idx < 0 || idx >= _candidates.Count)
        {
            TriggerNPC($"Please choose a number between 1 and {_candidates.Count}.");
            yield break;
        }

        _selectedCik  = _candidates[idx][0];
        _selectedName = _candidates[idx][1];

        Debug.Log($"[APIBridge] Selected candidate {query.Pick}: {_selectedName} (CIK {_selectedCik})");

        yield return ResolveAndFetch();
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region CIK lookup

    IEnumerator LookupCIK(string companyName)
    {
        _selectedCik = null;

        string encoded = UnityWebRequest.EscapeURL(companyName);
        string url     = $"{apiRoot}/cik?name={encoded}";
        Debug.Log($"[APIBridge] CIK lookup: {url}");

        using UnityWebRequest req = UnityWebRequest.Get(url);
        req.timeout = 10;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[APIBridge] CIK lookup failed: {req.error}");
            TriggerNPC("I couldn't reach the server. Please try again.");
            yield break;
        }

        string json = req.downloadHandler.text;
        Debug.Log($"[APIBridge] CIK response: {json}");

        List<string[]> results = ParseCIKResults(json);

        if (results == null || results.Count == 0)
        {
            TriggerNPC($"No data found. I couldn't find any company named \"{companyName}\". Please try a different name.");
        }
        else if (results.Count == 1)
        {
            _selectedCik  = results[0][0];
            _selectedName = results[0][1];
            Debug.Log($"[APIBridge] CIK resolved: {_selectedName} → {_selectedCik}");
            yield return ResolveAndFetch();
        }
        else
        {
            int show    = Math.Min(results.Count, 5);
            _candidates = results.GetRange(0, show);
            SetState(State.WaitingSelection);

            var sb = new StringBuilder();
            sb.Append($"I found {show} companies matching \"{companyName}\": ");
            for (int i = 0; i < show; i++)
                sb.Append($"{i + 1}. {_candidates[i][1]}. ");
            sb.Append("Which one would you like?");

            TriggerNPC(sb.ToString());
        }
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Resolve and fetch

    /// <summary>
    /// Single decision point once a CIK is known.
    /// Fetches immediately if FP and FY are both present, otherwise asks for what's missing.
    /// </summary>
    IEnumerator ResolveAndFetch()
    {
        bool hasFp = !string.IsNullOrEmpty(_pendingFp);
        bool hasFy = !string.IsNullOrEmpty(_pendingFy);

        if (hasFp && hasFy)
        {
            yield return FetchAndVisualize(_selectedCik, _pendingFp, _pendingFy);
            ResetState();
        }
        else
        {
            SetState(State.WaitingFiscalInfo);
            TriggerNPC(BuildMissingMessage());
        }
    }

    /// <summary>
    /// Builds a contextual NPC prompt that asks only for what is currently missing.
    /// </summary>
    string BuildMissingMessage()
    {
        bool hasFp = !string.IsNullOrEmpty(_pendingFp);
        bool hasFy = !string.IsNullOrEmpty(_pendingFy);

        if (!hasFp && !hasFy)
            return $"I found {_selectedName}. Please tell me the fiscal period and year you want.";
        if (!hasFp)
            return $"Got the year {_pendingFy}. Which fiscal period would you like? Q1, Q2, Q3, or Q4.";
        if (!hasFy)
            return $"Got the period {_pendingFp}. Which fiscal year would you like?";

        // Should never reach here but safe fallback
        return $"Ready to look up {_selectedName}. Please confirm the fiscal period and year.";
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region Data fetch + visualise

    IEnumerator FetchAndVisualize(string cik, string fp, string fy)
    {
        // Hard guard — should never be reached due to ResolveAndFetch checks
        if (string.IsNullOrEmpty(cik) || string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(fy))
        {
            Debug.LogError($"[APIBridge] FetchAndVisualize called with missing data: CIK={cik} FP={fp} FY={fy}");
            TriggerNPC("I'm missing some information. Could you tell me the fiscal period and year?");
            SetState(State.WaitingFiscalInfo);
            yield break;
        }

        _fetchInProgress = true;

        string url = $"{apiRoot}/data?ciks={cik}&fp={fp}&fy={fy}";
        Debug.Log($"[APIBridge] GET {url}");

        UnityWebRequest req = UnityWebRequest.Get(url);
        req.timeout = 10;

        // TriggerNPC a loading message so the user isn't left in silence
        TriggerNPC($"Please wait while I retrieve the data.");

        try
        {
            yield return req.SendWebRequest();
        }
        finally
        {
            _fetchInProgress = false;
            req.Dispose();
        }

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[APIBridge] Data fetch failed: {req.error}");
            TriggerNPC("I had trouble fetching the financial data. Please try again.");
            yield break;
        }

        string json = req.downloadHandler.text;
        Debug.Log($"[APIBridge] Data response received ({json.Length} chars)");

        // Check for empty or null response body
        string trimmed = json.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "[]" || trimmed == "{}" || trimmed == "null")
        {
            Debug.LogWarning($"[APIBridge] Empty response for CIK={cik} FP={fp} FY={fy}");
            TriggerNPC($"No data found. {_selectedName} does not have records for {fp} {fy}. Please try a different period or year.");
            SetState(State.WaitingFiscalInfo);
            yield break;
        }

        try
        {
            bool dataFound = Visualize(cik, fp, fy, json);
            if (!dataFound)
            {
                TriggerNPC($"No data found. {_selectedName} returned empty figures for {fp} {fy}. Try a different period or year.");
                SetState(State.WaitingFiscalInfo);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[APIBridge] Visualize error: {ex.Message}\n{ex.StackTrace}");
            TriggerNPC("I had trouble reading the financial data. Please try again.");
        }
    }

    bool Visualize(string cik, string fp, string fy, string json)
    {
        string name    = ExtractJsonString(json, "name")    ?? (_selectedName ?? cik);
        float  revenue = ExtractJsonNumber(json, "revenue");
        float  expense = ExtractJsonNumber(json, "expense");
        float  tax     = ExtractJsonNumber(json, "taxes");
        float  eps     = ExtractJsonNumber(json, "eps");

        // If all financial values are zero treat it as no data
        if (revenue == 0f && expense == 0f && tax == 0f && eps == 0f)
        {
            Debug.LogWarning($"[APIBridge] All values zero for CIK={cik} FP={fp} FY={fy} — treating as no data.");
            return false;
        }

        string label = $"{name}\n{fp} {fy}";
        Debug.Log($"[APIBridge] Visualizing \"{label}\" Rev={revenue:N0} Exp={expense:N0} Tax={tax:N0} EPS={eps}");

        if (companyLoader != null)
            companyLoader.SpawnAPICompany(label, revenue, expense, tax, eps);
        else
            Debug.LogWarning("[APIBridge] companyLoader not assigned — block not spawned.");

        return true;
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region NPC trigger

    void TriggerNPC(string message)
    {
        if (_npc == null)
        {
            Debug.LogWarning("[APIBridge] No ConvaiNPC in scene — cannot trigger speech.");
            return;
        }

        Debug.Log($"[APIBridge] TriggerSpeech → \"{message}\"");
        _npc.TriggerSpeech(message);
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region State reset

    void ResetState()
    {
        _state           = State.Idle;
        _candidates      = null;
        _selectedCik     = null;
        _selectedName    = null;
        _fetchInProgress = false;
        _queryProcessing = false;

        if (clearAfterFetch)
        {
            _pendingFp = null;
            _pendingFy = null;
        }

        Debug.Log("[APIBridge] State reset to Idle.");
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region CIK result parser

    /// Parses: [["0001288776","GOOGLE INC."],["0001652044","GOOGLE LLC"]]
    List<string[]> ParseCIKResults(string json)
    {
        var results = new List<string[]>();

        if (string.IsNullOrWhiteSpace(json)) return results;

        var matches = Regex.Matches(json,
            @"\[\s*""([^""]+)""\s*,\s*""([^""]+)""\s*\]");

        foreach (Match m in matches)
            results.Add(new[] { m.Groups[1].Value, m.Groups[2].Value });

        return results;
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    #region JSON micro-parser

    static string ExtractJsonString(string json, string key)
    {
        var m = Regex.Match(json,
            $@"""{Regex.Escape(key)}""\s*:\s*""([^""]*?)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    static float ExtractJsonNumber(string json, string key)
    {
        // Try quoted number first: "key": "123.45"
        var m = Regex.Match(json,
            $@"""{Regex.Escape(key)}""\s*:\s*""([-\d.eE+]+)""");
        if (m.Success) return ParseRaw(m.Groups[1].Value);

        // Try unquoted number: "key": 123.45
        m = Regex.Match(json,
            $@"""{Regex.Escape(key)}""\s*:\s*([-\d.eE+]+)");
        if (m.Success) return ParseRaw(m.Groups[1].Value);

        return 0f;
    }

    static float ParseRaw(string s) =>
        float.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float v) ? v : 0f;

    #endregion
}

/*
 ╔══════════════════════════════════════════════════════════════════════════════╗
 ║  CONVAI SYSTEM PROMPT  (convai.ai → your character → Backstory)            ║
 ╚══════════════════════════════════════════════════════════════════════════════╝

You are CyberAccounting AI, a financial data assistant inside a VR visualisation
environment. Your job is to help users explore company financial data shown as
3-D stacked blocks.

HOW TO HANDLE REQUESTS:
There are two types of input you will receive:

1. USER SPEECH — the user talking to you directly. Always engage with this
   naturally and helpfully. Extract what they are asking for and respond
   accordingly.

2. SYSTEM MESSAGES — trigger messages sent from Unity containing instructions
   or status updates. These always start with phrases like "Got it", "No data
   found", "I found", or "Selected". Deliver these to the user clearly and
   stop. Do not add commentary.

Never confuse these two. User speech always gets a natural helpful response.
System messages are always delivered as-is.

KEY FINANCIAL TERMS:
- CIK: unique SEC identifier for a company (e.g. 0001288776 = Google)
- FP:  fiscal period — Q1, Q2, Q3, Q4, or FY (full year)
- FY:  fiscal year (4-digit, e.g. 2024)

BLOCK COLOURS:
- Red   = Expenses     (or excess above revenue in a loss scenario)
- Blue  = Taxes
- Green = Net Income   (or Revenue in a loss scenario)

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
STRUCTURED OUTPUT — CRITICAL
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

For every message from a user, include a QUERY tag on its own line BEFORE
your spoken response. This tag is read by the Unity system and must always
be present.

FORMAT:
[QUERY company="<name or MISSING>" fp="<Q1/Q2/Q3/Q4/FY or MISSING>" fy="<year or MISSING>" action="<action>"]

ACTION VALUES:
- "fetch"   — user provided company, fp, and fy. Ready to retrieve.
- "search"  — user mentioned a company. Need fp and/or fy.
- "waiting" — user provided fp and/or fy but no company yet.
- "select"  — user is choosing from a list. Add pick="<number>".
- "cancel"  — user wants to stop or start over.
- "unclear" — no financial intent detected.

RULES:
- Extract the company name exactly as the user said it. Never include
  phrases like "financial data for" or "show me" in the company field.
- Set any unknown field to MISSING. Never guess or invent values.
- The tag must be on its own line, separate from your spoken response.
- Never speak the tag aloud or include it in your spoken response.
- Never assume a fiscal period or year the user has not explicitly stated.
- Never say "Loading..." unless a system message tells you data was found.
- When asked for fiscal info, ask openly — do not suggest a specific
  example like "Q1 2024".

EXAMPLES:
User: "show me financial data for Papa Johns quarter 1 2022"
Tag:  [QUERY company="Papa Johns" fp="Q1" fy="2022" action="fetch"]
Spoken: "Got it, looking up Papa Johns for Q1 2022."

User: "show me Google"
Tag:  [QUERY company="Google" fp="MISSING" fy="MISSING" action="search"]
Spoken: "I found Google. What fiscal period and year would you like?"

User: "Q1 2023"
Tag:  [QUERY company="MISSING" fp="Q1" fy="2023" action="waiting"]
Spoken: "Got Q1 2023. Which company are you interested in?"

User: "the second one"
Tag:  [QUERY company="MISSING" fp="MISSING" fy="MISSING" action="select" pick="2"]
Spoken: "Selected option 2."

User: "never mind"
Tag:  [QUERY company="MISSING" fp="MISSING" fy="MISSING" action="cancel"]
Spoken: "Okay, starting over. Which company would you like?"

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
CRITICAL RULES
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

- Never assume or invent a fiscal period or year.
- Never say "Loading..." or confirm a fetch unless a system message says so.
- Never add commentary to system messages — deliver them and stop.
- Never mention server errors unless a system message says the server failed.
- When data is not found, say it was unavailable and ask for a different
  period or year. Do not speculate about why.
- Never ask more than one question at a time.
- Always respond concisely — this is a voice interface.
*/