using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        static async Task<bool> SchedulerTestTask(Task task) {await task.ConfigureAwait(false);return true;}
        async Task<AgentScheduleTrigger> WaitForScheduledUiRun() {
            for(int i=0;i<150;i++) {
                List<AgentScheduleTrigger> runs=await durableHost.Scheduler.RunsAsync().ConfigureAwait(false);
                foreach(AgentScheduleTrigger run in runs)if(run.Status==ScheduleTriggerStatus.WaitingForApproval)return run;
                await Task.Delay(20).ConfigureAwait(false);
            }
            throw new Exception("Scheduler UI isolated run did not reach permission.");
        }
        void TestSchedulerUi(List<string> checks,string output) {
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(durableHost.SchedulerHost.Status!=SchedulerHostStatus.Stopped)throw new Exception("Scheduler must require manual host start.");
            durableFile.Text="scheduler-ui.txt";durableText.Text="Synthetic scheduler UI fixture";
            Find<TextBox>("ScheduleTime").Text=AgentScheduleRules.FormatLocal(DateTime.Now);
            Find<Expander>("SchedulerPanel").IsExpanded=true;WaitForMemoryUi();
            Find<Button>("ScheduleCreate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            List<AgentSchedule> plans=WaitForAgent(durableHost.Scheduler.ListAsync());
            if(plans.Count!=1||WaitForAgent(durableHost.Scheduler.RunsAsync()).Count!=0)throw new Exception("UI save should persist plan without starting host.");
            panel.UpdateLayout();Find<Expander>("SchedulerPanel").BringIntoView();panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-scheduler-plan.png"));
            Find<Button>("SchedulerStart").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            panel.Hide();AgentScheduleTrigger run=WaitForAgent(WaitForScheduledUiRun());ShowPanel();
            WaitForAgent(SchedulerTestTask(RefreshSchedulerAsync()));WaitForAgent(SchedulerTestTask(RefreshWorkspaceTasksAsync()));
            if(File.Exists(Path.Combine(durableHost.FileDirectory,plans[0].FilePrefix+"-"+run.Id.Substring(0,16)+".txt")))throw new Exception("Scheduler bypassed permission.");
            ShowWorkspacePermission(run.TaskId);panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-scheduler-permission.png"));
            ClickPermission(run.TaskId,true);WaitForMemoryUi();WaitForAgent(SchedulerTestTask(durableHost.Scheduler.SynchronizeAsync()));
            if(!File.Exists(Path.Combine(durableHost.FileDirectory,plans[0].FilePrefix+"-"+run.Id.Substring(0,16)+".txt"))||
                WaitForAgent(durableHost.Scheduler.RunsAsync())[0].Status!=ScheduleTriggerStatus.Succeeded)throw new Exception("Scheduler UI did not use shared bound approval and persist result.");
            Find<Button>("SchedulerStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(durableHost.SchedulerHost.Status!=SchedulerHostStatus.Stopped)throw new Exception("Scheduler UI did not stop host.");
            Find<Expander>("SchedulerPanel").IsExpanded=false;workspace.OpenDrawer(null);
            checks.Add("PASS Scheduler V1 UI explicitly saves plan, manually starts/stops host, runs with panel hidden, uses conversation permission card and persists real isolated file result");
        }
    }
}
