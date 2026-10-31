using LabeledRecorder;
using UnityEngine;

/// <summary>
/// Every shape of label call, in the places you would really put them.
/// </summary>
/// <remarks>
/// <para>
/// Drop this on any GameObject and enter play mode with a Unity Recorder capture running. The label file
/// appears next to the video when the recording stops.
/// </para>
/// <para>
/// Nothing here is guarded by <c>#if UNITY_EDITOR</c> and nothing needs to be: every <c>GameLabel</c> call,
/// and every expression passed to one, is deleted by the compiler when this script is built into a player.
/// </para>
/// </remarks>
public class LabeledRecorderQuickStart : MonoBehaviour
{
    [Header("Fake game state, so the sample has something to report")]
    [SerializeField] float m_Health = 100f;
    [SerializeField] int m_Score;
    [SerializeField] string m_Weapon = "pistol";

    bool m_InBossFight;
    float m_NextBeat;
    int m_Beat;

    void Start()
    {
        // Facts about the whole take. These land in session.meta, and are what lets you tell runs apart
        // later: which build, which difficulty, which seed.
        GameLabel.Meta("difficulty", "nightmare");
        GameLabel.Meta("build", Application.version);

        // Chapters give the recording its coarse structure. An editing agent uses them to avoid cutting
        // across a hard scene change, and to make sure a trailer covers the whole arc.
        GameLabel.Chapter("Arena");
    }

    void Update()
    {
        // Signals: call these every frame. A sample is written only when the value actually changes, so the
        // label file stays small and still answers "what was the player's health at 00:01:12?".
        GameLabel.Set("player.health", m_Health, 5f);   // ignore moves smaller than 5
        GameLabel.Set("player.weapon", m_Weapon);
        GameLabel.Set("player.score", m_Score);

        // Stand-ins for real gameplay triggers, so the sample produces labels on its own. In your game these
        // calls belong wherever the event actually happens.
        m_NextBeat -= Time.deltaTime;
        if (m_NextBeat > 0f)
            return;

        m_NextBeat = 3f;
        m_Beat++;

        OnKill(m_Beat % 2 == 0 ? "rifle" : "pistol", m_Beat % 3 == 0);

        if (m_Beat == 4)
            ToggleBossFight();

        if (m_Beat == 7)
        {
            GameLabel.Highlight("something worth clipping", 0.9f);
            ToggleBossFight();
        }
    }

    void OnKill(string weapon, bool headshot)
    {
        m_Score += 150;

        // A point event. The dotted name gives it the category "combat", which is how an agent asks for all
        // the action in a game it knows nothing about.
        GameLabel.Event("combat.kill", "weapon", weapon, "headshot", headshot);
    }

    void ToggleBossFight()
    {
        if (m_InBossFight)
        {
            // Fields set on End are merged into the span, so outcomes belong here.
            GameLabel.End("boss.fight", "won", m_Health > 0f);
            m_InBossFight = false;
            return;
        }

        // A span: something that lasts. The writer pairs Begin and End by name, handles nesting, and records
        // the duration, which makes the whole fight a ready-made clip range.
        GameLabel.Begin("boss.fight", "name", "Gravelord");
        m_InBossFight = true;
    }

    System.Collections.IEnumerator PlayIntroCutscene()
    {
        // The using form closes the span for you. It is the one call that cannot be compiled away entirely,
        // so on a hot path prefer Begin and End.
        using (GameLabel.Span("cutscene.intro"))
        {
            yield return new WaitForSeconds(4f);
        }
    }

    void OnExpensivePayloadExample()
    {
        // You never need this to make labelling cheap. Use it only to skip work you would otherwise do just
        // to build a payload.
        if (GameLabel.IsRecording)
            GameLabel.Event("wave.cleared", "enemiesLeft", CountEnemiesTheSlowWay());
    }

    int CountEnemiesTheSlowWay()
    {
        return FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Length;
    }
}
