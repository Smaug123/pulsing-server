namespace PulsingServer

type private ServerAgentMessage<'info> =
    | Read of AsyncReplyChannel<unit> * ('info -> unit)
    | Write of AsyncReplyChannel<unit> * 'info

type ServerAgent<'info> = private | ServerAgent of MailboxProcessor<ServerAgentMessage<'info>>

[<RequireQualifiedAccess>]
module ServerAgent =

    /// Create a ServerAgent which is ready to take new information
    /// and is ready to give responses.
    let make<'info> (initialInfo : 'info) : ServerAgent<'info> =
        let rec loop (info : 'info) (mailbox : MailboxProcessor<ServerAgentMessage<'info>>) =
            async {
                match! mailbox.Receive () with
                | Read (channel, reply) ->
                    reply info
                    channel.Reply ()
                    return! loop info mailbox
                | Write (channel, info) ->
                    channel.Reply ()
                    return! loop info mailbox
            }

        loop initialInfo
        |> MailboxProcessor.Start
        |> ServerAgent

    /// Write new information to this ServerAgent's internal store.
    /// The write may take place any time after this function returns;
    /// but the write is guaranteed to have been performed once the Async completes.
    let post<'info> (info : 'info) (ServerAgent agent) : Async<unit> =
        agent.PostAndAsyncReply (fun channel -> Write (channel, info))

    /// Ask the ServerAgent to give back its info.
    /// The function returns an async once it has submitted the request to the ServerAgent;
    /// the async returns once the ServerAgent has finished responding.
    let giveNextResponse<'info> (ServerAgent agent) : Async<'info> =
        let mutable answer = Unchecked.defaultof<'info>
        let result = agent.PostAndAsyncReply (fun channel -> Read (channel, fun info -> answer <- info))
        async {
            do! result
            return answer
        }