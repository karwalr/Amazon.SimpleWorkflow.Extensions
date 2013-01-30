﻿module SWF.Extensions.Core.Workflow

open System

open Amazon.SimpleWorkflow
open Amazon.SimpleWorkflow.Extensions
open Amazon.SimpleWorkflow.Model

open SWF.Extensions.Core.Model

type Activity (name, description : string, task : string -> string, 
               taskHeartbeatTimeout        : Seconds,
               taskScheduleToStartTimeout  : Seconds,
               taskStartToCloseTimeout     : Seconds,
               taskScheduleToCloseTimeout  : Seconds,
               ?taskList) = 
    let taskList = defaultArg taskList (name + "TaskList")
    
    member this.Name                        = name
    member this.Description                 = description
    member this.Task                        = task
    member this.TaskList                    = TaskList(Name = taskList)
    member this.TaskHeartbeatTimeout        = taskHeartbeatTimeout
    member this.TaskScheduleToStartTimeout  = taskScheduleToStartTimeout
    member this.TaskStartToCloseTimeout     = taskStartToCloseTimeout
    member this.TaskScheduleToCloseTimeout  = taskScheduleToCloseTimeout

type StageAction = 
    | ScheduleActivity  of Activity

type Stage =
    {
        Id      : int
        Action  : StageAction
        Version : string
    }
    
[<AutoOpen>]
module Helper =
    let nullOrWs = String.IsNullOrWhiteSpace

[<RequireQualifiedAccess>]
module HistoryEvents =
    let rec getWorkflowInput (events : HistoryEvent list) = 
        match events with
        | []    -> None
        | { EventType = WorkflowExecutionStarted(_, _, _, _, _, _, _, input, _, _) }::_ 
                -> input
        | _::tl -> getWorkflowInput tl

type Workflow (domain, name, description, version, ?taskList, 
               ?activities              : Activity list,
               ?taskStartToCloseTimeout : Seconds,
               ?execStartToCloseTimeout : Seconds,
               ?childPolicy             : ChildPolicy) =
    do if nullOrWs domain then nullArg "domain"
    do if nullOrWs name   then nullArg "name"

    let onDecisionTaskError = new Event<Exception>()
    let onActivityTaskError = new Event<Exception>()

    let taskList = new TaskList(Name = defaultArg taskList (name + "TaskList"))

    let activities = defaultArg activities [] 
    let stages = 
        activities
        |> List.rev
        |> List.mapi (fun i activity -> { Id = i; Action = ScheduleActivity activity; Version = sprintf "%s.%d" name i })

    /// registers the workflow and activity types
    let register (clt : Amazon.SimpleWorkflow.AmazonSimpleWorkflowClient) = 
        let registerActivities stages = async {
            let req = ListActivityTypesRequest(Domain = domain).WithRegistrationStatus(string Registered)
            let! res = clt.ListActivityTypesAsync(req)

            let existing = res.ListActivityTypesResult.ActivityTypeInfos.TypeInfos
                           |> Seq.map (fun info -> info.ActivityType.Name, info.ActivityType.Version)
                           |> Set.ofSeq

            let activities = stages 
                             |> List.choose (function 
                                | { Id = id; Action = ScheduleActivity(activity); Version = version } when not <| existing.Contains(activity.Name, version)
                                    -> Some(id, activity, version) 
                                | _ -> None)

            for (id, activity, version) in activities do
                let req = RegisterActivityTypeRequest(Domain = domain, Name = activity.Name)
                            .WithDescription(activity.Description)
                            .WithDefaultTaskList(activity.TaskList)
                            .WithVersion(version)
                            .WithDefaultTaskHeartbeatTimeout(str activity.TaskHeartbeatTimeout)
                            .WithDefaultTaskScheduleToStartTimeout(str activity.TaskScheduleToStartTimeout)
                            .WithDefaultTaskStartToCloseTimeout(str activity.TaskStartToCloseTimeout)
                            .WithDefaultTaskScheduleToCloseTimeout(str activity.TaskScheduleToCloseTimeout)

                do! clt.RegisterActivityTypeAsync(req) |> Async.Ignore
        }

        let registerWorkflow () = async {
            let req = ListWorkflowTypesRequest(Domain = domain, Name = name).WithRegistrationStatus(string Registered)
            let! res = clt.ListWorkflowTypesAsync(req)

            // only register the workflow if it doesn't exist already
            if res.ListWorkflowTypesResult.WorkflowTypeInfos.TypeInfos.Count = 0 then
                let req = RegisterWorkflowTypeRequest(Domain = domain, Name = name)
                            .WithDescription(description)
                            .WithVersion(version)
                            .WithDefaultTaskList(taskList)
                taskStartToCloseTimeout ?-> (str >> req.WithDefaultTaskStartToCloseTimeout)
                execStartToCloseTimeout ?-> (str >> req.WithDefaultExecutionStartToCloseTimeout)
                childPolicy             ?-> (str >> req.WithDefaultChildPolicy)

                do! clt.RegisterWorkflowTypeAsync(req) |> Async.Ignore
        }

        seq { yield registerActivities stages; yield registerWorkflow() }
        |> Async.Parallel
        |> Async.RunSynchronously

    /// recursively tries to get the history event with the specified event Id
    let rec getEventType eventId = function
        | { EventId = eventId'; EventType = eventType }::tl when eventId = eventId' -> eventType
        | hd::tl -> getEventType eventId tl

    /// tries to get the nth (zero-index) stage
    let getStage n = if n >= stages.Length then None else List.nth stages n |> Some

    /// schedules the nth (zero-indexed) stage
    let scheduleStage n input =
        match getStage n with
        | Some({ Id = id; Action = ScheduleActivity(activity); Version = version }) 
               -> let activityType = ActivityType(Name = activity.Name, Version = version)
                  let decision = ScheduleActivityTask(str id, activityType,
                                                      Some activity.TaskList, 
                                                      input,
                                                      Some activity.TaskHeartbeatTimeout, 
                                                      Some activity.TaskScheduleToStartTimeout, 
                                                      Some activity.TaskStartToCloseTimeout, 
                                                      Some activity.TaskScheduleToCloseTimeout,
                                                      None)
                  [| decision |], ""
        | None -> [| CompleteWorkflowExecution None |], ""

    let rec decide (events : HistoryEvent list) input =
        match events with
        | [] -> scheduleStage 0 input
        | { EventType = ActivityTaskCompleted(scheduledEvtId, _, result) }::tl ->
            // find the event for when the activity was scheduled
            let (ActivityTaskScheduled(activityId, _, _, _, _, _, _, _, _, _)) = getEventType scheduledEvtId tl
            
            // schedule the next stage in the workflow
            scheduleStage (int activityId + 1) result
        | hd::tl -> decide tl input

    let decider (task : DecisionTask) = 
        let input = HistoryEvents.getWorkflowInput task.Events
        decide task.Events input

    let startDecisionWorker clt  = DecisionWorker.Start(clt, domain, taskList.Name, decider, onDecisionTaskError.Trigger)
    let startActivityWorkers (clt : Amazon.SimpleWorkflow.AmazonSimpleWorkflowClient) = 
        stages 
        |> List.choose (function | { Action = ScheduleActivity(activity) } -> Some activity | _ -> None)
        |> List.iter (fun activity -> 
            let heartbeat = TimeSpan.FromSeconds(float activity.TaskHeartbeatTimeout)
            ActivityWorker.Start(clt, domain, activity.TaskList.Name,
                                 activity.Task, onActivityTaskError.Trigger, 
                                 heartbeat))

    member private this.Attach (activity) = Workflow(domain, name, description, version, taskList.Name, 
                                                     activity :: activities,
                                                     ?taskStartToCloseTimeout = taskStartToCloseTimeout,
                                                     ?execStartToCloseTimeout = execStartToCloseTimeout,
                                                     ?childPolicy             = childPolicy)        

    [<CLIEvent>]
    member this.OnDecisionTaskError = onDecisionTaskError.Publish

    [<CLIEvent>]
    member this.OnActivityTaskError = onActivityTaskError.Publish

    member this.Start swfClt = 
        register swfClt |> ignore
        startDecisionWorker  swfClt
        startActivityWorkers swfClt

    static member (++>) (workflow : Workflow, activity) = workflow.Attach(activity)