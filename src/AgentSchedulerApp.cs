using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Tamago {
    public sealed partial class PetApp {
        DispatcherTimer schedulerRefresh;
        bool schedulerRefreshing;
        void InitializeSchedulerPanel() {
            Find<TextBox>("ScheduleTime").Text=AgentScheduleRules.FormatLocal(DateTime.Now.AddMinutes(5));
            ComboBox zones=Find<ComboBox>("ScheduleZone");
            foreach(TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones()) {
                ComboBoxItem item=new ComboBoxItem {Content=zone.DisplayName,Tag=zone.Id};zones.Items.Add(item);
                if(zone.Id==TimeZoneInfo.Local.Id)zones.SelectedItem=item;
            }
            Find<Button>("ScheduleCreate").Click+=delegate {SchedulerUi(async delegate {
                ComboBoxItem zone=zones.SelectedItem as ComboBoxItem;
                string prefix=durableFile.Text.EndsWith(".txt",StringComparison.OrdinalIgnoreCase)?durableFile.Text.Substring(0,durableFile.Text.Length-4):durableFile.Text;
                await durableHost.Scheduler.CreateAsync(Find<TextBox>("ScheduleTitle").Text,Find<TextBox>("ScheduleTime").Text,
                    zone==null?null:(string)zone.Tag,(ScheduleRecurrence)Find<ComboBox>("ScheduleRule").SelectedIndex,prefix,durableText.Text);
                await RefreshSchedulerAsync();
            });};
            Find<Button>("SchedulerStart").Click+=delegate {SchedulerUi(async delegate {durableHost.SchedulerHost.Start();await RefreshSchedulerAsync();});};
            Find<Button>("SchedulerStop").Click+=delegate {SchedulerUi(async delegate {await durableHost.SchedulerHost.StopAsync();await RefreshSchedulerAsync();});};
            Find<Button>("SchedulerPause").Click+=delegate {SchedulerUi(async delegate {durableHost.SchedulerHost.Pause();await RefreshSchedulerAsync();});};
            Find<Button>("SchedulerResume").Click+=delegate {SchedulerUi(async delegate {durableHost.SchedulerHost.Resume();await RefreshSchedulerAsync();});};
            Find<Button>("SchedulePause").Click+=delegate {SchedulerPlanControl(false,false);};
            Find<Button>("ScheduleResume").Click+=delegate {SchedulerPlanControl(true,false);};
            Find<Button>("ScheduleCancel").Click+=delegate {SchedulerPlanControl(false,true);};
            Find<Button>("ScheduleRunPause").Click+=delegate {SchedulerRunControl(true);};
            Find<Button>("ScheduleRunResume").Click+=delegate {SchedulerRunControl(false);};
            Find<Button>("ScheduleRunCancel").Click+=delegate {SchedulerUi(async delegate {
                ComboBoxItem item=Find<ComboBox>("ScheduleRuns").SelectedItem as ComboBoxItem;
                if(item!=null&&((AgentScheduleTrigger)item.Tag).TaskId!=null)await durableHost.Scheduler.CancelTaskAsync(((AgentScheduleTrigger)item.Tag).TaskId);
                await RefreshSchedulerAsync();await RefreshWorkspaceTasksAsync();
            });};
            Find<Expander>("SchedulerPanel").Expanded+=delegate {SchedulerUi(RefreshSchedulerAsync);};
            schedulerRefresh=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};
            schedulerRefresh.Tick+=async delegate {
                if(quitting||durableHost==null||durableBusy||schedulerRefreshing||!panel.IsVisible)return;
                schedulerRefreshing=true;
                try {await RefreshSchedulerAsync();await RefreshWorkspaceTasksAsync();}
                catch {Find<TextBlock>("SchedulerStatus").Text="调度存储不可用；请退出宿主后检查记录。";}
                finally {schedulerRefreshing=false;}
            };
            schedulerRefresh.Start();
        }
        void SchedulerUi(Func<Task> action) {durablePending=DurableActionAsync(action);}
        void SchedulerPlanControl(bool enable,bool cancel) {
            SchedulerUi(async delegate {
                ComboBoxItem item=Find<ComboBox>("Schedules").SelectedItem as ComboBoxItem;if(item==null)return;
                AgentSchedule plan=(AgentSchedule)item.Tag;
                if(cancel)await durableHost.Scheduler.CancelScheduleAsync(plan.Id);else await durableHost.Scheduler.SetEnabledAsync(plan.Id,enable);
                await RefreshSchedulerAsync();await RefreshWorkspaceTasksAsync();
            });
        }
        void SchedulerRunControl(bool pause) {
            SchedulerUi(async delegate {
                ComboBoxItem item=Find<ComboBox>("ScheduleRuns").SelectedItem as ComboBoxItem;if(item==null)return;
                await durableHost.Scheduler.SetRunPausedAsync(((AgentScheduleTrigger)item.Tag).Id,pause);await RefreshSchedulerAsync();
            });
        }
        static string ScheduleHostLabel(SchedulerHostStatus status) {
            switch(status) {
                case SchedulerHostStatus.Running:return "运行中";
                case SchedulerHostStatus.Paused:return "已暂停分发";
                case SchedulerHostStatus.NeedsReview:return "等待人工核对 · 已阻止新分发";
                case SchedulerHostStatus.Faulted:return "安全停止 · 存储或运行异常，请重启并核对";
                default:return "已停止 · 需要手动启动";
            }
        }
        async Task RefreshSchedulerAsync() {
            if(durableHost==null)return;
            List<AgentSchedule> plans=await durableHost.Scheduler.ListAsync();List<AgentScheduleTrigger> runs=await durableHost.Scheduler.RunsAsync();
            if(quitting)return;
            Find<TextBlock>("SchedulerStatus").Text="本地调度："+ScheduleHostLabel(durableHost.SchedulerHost.Status);
            ComboBox list=Find<ComboBox>("Schedules");ComboBoxItem old=list.SelectedItem as ComboBoxItem;string id=old==null?null:((AgentSchedule)old.Tag).Id;
            list.Items.Clear();foreach(AgentSchedule plan in plans) {
                ComboBoxItem item=new ComboBoxItem {Tag=plan,Content=plan.Title+" · "+(plan.Cancelled?"已取消":plan.NextLocal==null?"已触发":plan.Enabled?plan.NextLocal:"已暂停")};
                list.Items.Add(item);if(plan.Id==id)list.SelectedItem=item;
            }
            if(list.SelectedItem==null&&list.Items.Count>0)list.SelectedIndex=0;
            list=Find<ComboBox>("ScheduleRuns");old=list.SelectedItem as ComboBoxItem;id=old==null?null:((AgentScheduleTrigger)old.Tag).Id;
            list.Items.Clear();foreach(AgentScheduleTrigger run in runs) {
                AgentSchedule plan=plans.Find(delegate(AgentSchedule s){return s.Id==run.ScheduleId;});
                string label=RunLabel(run.Status);
                ComboBoxItem item=new ComboBoxItem {Tag=run,Content=plan.Title+" · "+run.LocalOccurrence+" · "+label+(run.OccurrenceCount>1?"（"+run.OccurrenceCount+" 次）":"")};
                list.Items.Add(item);if(run.Id==id)list.SelectedItem=item;
            }
            if(list.SelectedItem==null&&list.Items.Count>0)list.SelectedIndex=list.Items.Count-1;
            SchedulerHostStatus status=durableHost.SchedulerHost.Status;
            Find<Button>("SchedulerStart").IsEnabled=status==SchedulerHostStatus.Stopped;
            Find<Button>("SchedulerPause").IsEnabled=status==SchedulerHostStatus.Running||status==SchedulerHostStatus.NeedsReview;
            Find<Button>("SchedulerResume").IsEnabled=status==SchedulerHostStatus.Paused;
            Find<Button>("SchedulerStop").IsEnabled=status!=SchedulerHostStatus.Stopped;
            SetAuditArchiveButtons();
        }
        static string RunLabel(ScheduleTriggerStatus state) {
            switch(state) {
                case ScheduleTriggerStatus.Missed:return "已跳过";
                case ScheduleTriggerStatus.Paused:return "已暂停";
                case ScheduleTriggerStatus.WaitingForApproval:return "等待确认";
                case ScheduleTriggerStatus.Queued:return "排队中";
                case ScheduleTriggerStatus.Running:return "执行中";
                case ScheduleTriggerStatus.Succeeded:return "已完成";
                case ScheduleTriggerStatus.Cancelled:return "已取消";
                case ScheduleTriggerStatus.NeedsReview:return "需要核对";
                case ScheduleTriggerStatus.TimedOut:return "预算耗尽";
                default:return "未完成";
            }
        }
    }
}
