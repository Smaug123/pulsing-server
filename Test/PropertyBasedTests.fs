namespace PulsingServer.Test

open PulsingServer
open NUnit.Framework
open FsUnitTyped

type AgentIndex = AgentIndex of int
type ReadIndex = ReadIndex of int

type Action<'info> =
    | ChangeData of 'info
    | BeginRead of AgentIndex
    | AwaitRead of ReadIndex

[<TestFixture>]
module TestProperties =

    let executeAction
        (ext : ExternalInfoProvider<'info>)
        (agents : ServerAgent<'info> array)
        ((readNumber : int), (awaitingRead : Map<ReadIndex, Async<'info>>))
        (action : Action<'info>)
        =
        match action with
        | BeginRead (AgentIndex i) ->
            let mutable answer = None
            let result = ServerAgent.giveNextResponse (fun resp -> answer <- Some resp) agents.[i]
            let output =
                async {
                    do! result
                    return Option.get answer
                }
            ext, agents, (readNumber + 1, Map.add (ReadIndex readNumber) output awaitingRead)
        | AwaitRead index ->
            awaitingRead.[index]
            |> Async.RunSynchronously
