﻿using Advanced_Combat_Tracker;
using FFXIV_ACT_Plugin;
using Newtonsoft.Json;
using RainbowMage.OverlayPlugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Tamagawa.EnmityPlugin;

namespace Cactbot {

  public class CactbotOverlay : OverlayBase<CactbotOverlayConfig>, Tamagawa.EnmityPlugin.Logger {
    private static int kFastTimerMilli = 16;
    private static int kSlowTimerMilli = 300;

    private SemaphoreSlim log_lines_semaphore_ = new SemaphoreSlim(1);
    // Not thread-safe, as OnLogLineRead may happen at any time. Use |log_lines_semaphore_| to access it.
    private List<string> log_lines_ = new List<string>(40);
    // Used on the fast timer to avoid allocing List every time.
    private List<string> last_log_lines_ = new List<string>(40);

    private SemaphoreSlim reset_notify_state_semaphore_ = new SemaphoreSlim(1);
    // When true, the update function should reset notify state back to defaults.
    bool reset_notify_state_ = false;

    private StringBuilder dispatch_string_builder_ = new StringBuilder(1000);
    JsonTextWriter dispatch_json_writer_;
    JsonSerializer dispatch_serializer_;

    private System.Timers.Timer fast_update_timer_;
    private FFXIVProcess ffxiv_;
    private FightTracker fight_tracker_;
    private WipeDetector wipe_detector_;
    private System.Threading.SynchronizationContext main_thread_sync_;

    public delegate void GameExistsHandler(JSEvents.GameExistsEvent e);
    public event GameExistsHandler OnGameExists;

    public delegate void GameActiveChangedHandler(JSEvents.GameActiveChangedEvent e);
    public event GameActiveChangedHandler OnGameActiveChanged;

    public delegate void ZoneChangedHandler(JSEvents.ZoneChangedEvent e);
    public event ZoneChangedHandler OnZoneChanged;

    public delegate void PlayerChangedHandler(JSEvents.PlayerChangedEvent e);
    public event PlayerChangedHandler OnPlayerChanged;

    public delegate void TargetChangedHandler(JSEvents.TargetChangedEvent e);
    public event TargetChangedHandler OnTargetChanged;

    public delegate void LogHandler(JSEvents.LogEvent e);
    public event LogHandler OnLogsChanged;

    public delegate void InCombatChangedHandler(JSEvents.InCombatChangedEvent e);
    public event InCombatChangedHandler OnInCombatChanged;

    public delegate void PlayerDiedHandler(JSEvents.PlayerDiedEvent e);
    public event PlayerDiedHandler OnPlayerDied;

    public delegate void PartyWipeHandler(JSEvents.PartyWipeEvent e);
    public event PartyWipeHandler OnPartyWipe;
    public void Wipe() {
      Advanced_Combat_Tracker.ActGlobals.oFormActMain.EndCombat(false);
      OnPartyWipe(new JSEvents.PartyWipeEvent());
    }

    public CactbotOverlay(CactbotOverlayConfig config)
        : base(config, config.Name) {
      main_thread_sync_ = System.Windows.Forms.WindowsFormsSynchronizationContext.Current;
      ffxiv_ = new FFXIVProcess(this);
      fight_tracker_ = new FightTracker(this);
      wipe_detector_ = new WipeDetector(this);
      dispatch_json_writer_ = new JsonTextWriter(new System.IO.StringWriter(dispatch_string_builder_));
      dispatch_serializer_ = JsonSerializer.CreateDefault();


      // Our own timer with a higher frequency than OverlayPlugin since we want to see
      // the effect of log messages quickly.
      fast_update_timer_ = new System.Timers.Timer();
      fast_update_timer_.Elapsed += (o, e) => {
        SendFastRateEvents();
      };
      fast_update_timer_.AutoReset = false;

      // Incoming events.
      Advanced_Combat_Tracker.ActGlobals.oFormActMain.OnLogLineRead += OnLogLineRead;

      // Outgoing JS events.
      OnGameExists += (e) => DispatchToJS(e);
      OnGameActiveChanged += (e) => DispatchToJS(e);
      OnZoneChanged += (e) => DispatchToJS(e);
      if (config.LogUpdatesEnabled) {
        OnLogsChanged += (e) => DispatchToJS(e);
      }
      OnPlayerChanged += (e) => DispatchToJS(e);
      OnTargetChanged += (e) => DispatchToJS(e);
      OnInCombatChanged += (e) => DispatchToJS(e);
      OnPlayerDied += (e) => DispatchToJS(e);
      OnPartyWipe += (e) => DispatchToJS(e);

      fast_update_timer_.Interval = kFastTimerMilli;
      fast_update_timer_.Start();
    }

    public override void Dispose() {
      fast_update_timer_.Stop();
      Advanced_Combat_Tracker.ActGlobals.oFormActMain.OnLogLineRead -= OnLogLineRead;
      base.Dispose();
    }

    public override void Navigate(string url) {
      base.Navigate(url);
      // Reset to defaults so on load the plugin gets notified about any non-defaults
      // consistently.
      this.reset_notify_state_semaphore_.Wait();
      this.reset_notify_state_ = true;
      this.reset_notify_state_semaphore_.Release();
    }

    private void OnLogLineRead(bool isImport, LogLineEventArgs args) {
      // isImport happens when somebody is importing old encounters and all the log lines are processed.
      // Don't need to send all of these to the overlay.
      if (isImport)
        return;
      log_lines_semaphore_.Wait();
      log_lines_.Add(args.logLine);
      log_lines_semaphore_.Release();
    }

    // This is called by the OverlayPlugin every 1s which is not often enough for us, so we
    // do our own update mechanism as well.
    protected override void Update() {
      SendSlowRateEvents();
    }

    // Sends an event called |event_name| to javascript, with an event.detail that contains
    // the fields and values of the |detail| structure.
    public void DispatchToJS(JSEvent e) {
      dispatch_string_builder_.Append("document.dispatchEvent(new CustomEvent('");
      dispatch_string_builder_.Append(e.EventName());
      dispatch_string_builder_.Append("', { detail: ");
      dispatch_serializer_.Serialize(dispatch_json_writer_, e);
      dispatch_string_builder_.Append(" }));");
      this.Overlay.Renderer.Browser.GetMainFrame().ExecuteJavaScript(dispatch_string_builder_.ToString(), null, 0);
      dispatch_string_builder_.Clear();
    }

    // Events that we want to update less often because they aren't are critical.
    private void SendSlowRateEvents() {
      // Handle startup and shutdown. And do not fire any events until the page has loaded and had a chance to
      // register its event handlers.
      //if (Overlay == null || Overlay.Renderer == null || Overlay.Renderer.Browser == null || Overlay.Renderer.Browser.IsLoading)
      //  return;

      // NOTE: This function runs on a different thread that SendFastRateEvents(), so anything it calls needs to be thread-safe!
    }

    // Events that we want to update as soon as possible.
    private void SendFastRateEvents() {
      // Handle startup and shutdown. And do not fire any events until the page has loaded and had a chance to
      // register its event handlers.
      if (Overlay == null || Overlay.Renderer == null || Overlay.Renderer.Browser == null || Overlay.Renderer.Browser.IsLoading) {
        fast_update_timer_.Interval = kSlowTimerMilli;
        fast_update_timer_.Start();
        return;
      }

      bool reset = false;
      this.reset_notify_state_semaphore_.Wait();
      reset = this.reset_notify_state_;
      this.reset_notify_state_ = false;
      this.reset_notify_state_semaphore_.Release();
      if (reset)
        this.notify_state_ = new NotifyState();

      bool game_exists = ffxiv_.FindProcess();
      if (game_exists != notify_state_.game_exists) {
        notify_state_.game_exists = game_exists;
        OnGameExists(new JSEvents.GameExistsEvent(game_exists));
      }

      bool game_active = game_active = ffxiv_.IsActive();
      if (game_active != notify_state_.game_active) {
        notify_state_.game_active = game_active;
        OnGameActiveChanged(new JSEvents.GameActiveChangedEvent(game_active));
      }

      // Silently stop sending other messages if the ffxiv process isn't around.
      if (!game_exists) {
        fast_update_timer_.Interval = kSlowTimerMilli;
        fast_update_timer_.Start();
        return;
      }

      // onInCombatChangedEvent: Fires when entering or leaving combat.
      bool in_combat = FFXIV_ACT_Plugin.ACTWrapper.InCombat;
      if (in_combat != notify_state_.in_combat) {
        notify_state_.in_combat = in_combat;
        OnInCombatChanged(new JSEvents.InCombatChangedEvent(in_combat));
      }

      // onZoneChangedEvent: Fires when the player changes their current zone.
      string zone_name = FFXIV_ACT_Plugin.ACTWrapper.CurrentZone;
      if (!zone_name.Equals(notify_state_.zone_name)) {
        notify_state_.zone_name = zone_name;
        OnZoneChanged(new JSEvents.ZoneChangedEvent(zone_name));
      }

      // The |player| can be null, such as during a zone change.
      Combatant player = ffxiv_.GetSelfCombatant();
      // The |target| can be null when no target is selected.
      Combatant target = ffxiv_.GetTargetCombatant();

      // onPlayerDiedEvent: Fires when the player dies. All buffs/debuffs are
      // lost.
      if (player != null) {
        bool dead = player.CurrentHP == 0;
        if (dead != notify_state_.dead) {
          notify_state_.dead = dead;
          if (dead)
            OnPlayerDied(new JSEvents.PlayerDiedEvent());
        }
      }

      // onPlayerChangedEvent: Fires when current player data changes.
      // TODO: Is this always true cuz it's only doing pointer comparison?
      if (player != null && player != notify_state_.player) {
        notify_state_.player = player;
        if ((JobEnum)player.Job == JobEnum.RDM) {
          var rdm = ffxiv_.GetRedMage();
          if (rdm != null) {
            var e = new JSEvents.PlayerChangedEvent(player);
            e.jobDetail = new JSEvents.PlayerChangedEvent.RedMageDetail(rdm.white, rdm.black);
            OnPlayerChanged(e);
          }
        } else {
          // No job-specific data.
          OnPlayerChanged(new JSEvents.PlayerChangedEvent(player));
        }
      }

      // onTargetChangedEvent: Fires when current target or their state changes.
      // TODO: Is this always true cuz it's only doing pointer comparison?
      if (target != notify_state_.target) {
        notify_state_.target = target;
        if (target != null)
          OnTargetChanged(new JSEvents.TargetChangedEvent(target));
        else
          OnTargetChanged(new JSEvents.TargetChangedEvent(null));
      }

      // onLogEvent: Fires when new combat log events from FFXIV are available. This fires after any
      // more specific events, some of which may involve parsing the logs as well.
      List<string> logs;
      log_lines_semaphore_.Wait();
      logs = log_lines_;
      log_lines_ = last_log_lines_;
      log_lines_semaphore_.Release();
      if (logs.Count > 0) {
        OnLogsChanged(new JSEvents.LogEvent(logs));
        logs.Clear();
      }
      last_log_lines_ = logs;

      fight_tracker_.Tick(DateTime.Now);

      fast_update_timer_.Interval = game_active ? kFastTimerMilli : kSlowTimerMilli;
      fast_update_timer_.Start();
    }

    public int IncrementAndGetPullCount(string boss_id) {
      for (int i = 0; i < Config.BossInfoList.Count; ++i) {
        if (Config.BossInfoList[i].id == boss_id) {
          int pull_count = Config.BossInfoList[i].pull_count + 1;
          Config.BossInfoList[i] = new BossInfo(boss_id, pull_count);
          return pull_count;
        }
      }
      Config.BossInfoList.Add(new BossInfo(boss_id, 1));
      return 1;
    }

    // Tamagawa.EnmityPlugin.Logger implementation.
    public void LogDebug(string format, params object[] args) {
      // The Log() method is not threadsafe. Since this is called from Timer threads,
      // it must post the task to the plugin main thread.
      main_thread_sync_.Post(
        (state) => { this.Log(LogLevel.Debug, format, args); },
        null);
    }
    public void LogError(string format, params object[] args) {
      // The Log() method is not threadsafe. Since this is called from Timer threads,
      // it must post the task to the plugin main thread.
      main_thread_sync_.Post(
        (state) => { this.Log(LogLevel.Error, format, args); },
        null);
    }
    public void LogWarning(string format, params object[] args) {
      // The Log() method is not threadsafe. Since this is called from Timer threads,
      // it must post the task to the plugin main thread.
      main_thread_sync_.Post(
        (state) => { this.Log(LogLevel.Warning, format, args); },
        null);
    }
    public void LogInfo(string format, params object[] args) {
      // The Log() method is not threadsafe. Since this is called from Timer threads,
      // it must post the task to the plugin main thread.
      main_thread_sync_.Post(
        (state) => { this.Log(LogLevel.Info, format, args); },
        null);
    }

    // State that is tracked and sent to JS when it changes.
    private class NotifyState {
      public bool game_exists = false;
      public bool game_active = false;
      public bool in_combat = false;
      public bool dead = false;
      public string zone_name = "";
      public Combatant player = null;
      public Combatant target = null;
    }
    private NotifyState notify_state_ = new NotifyState();
  }

}  // namespace Cactbot