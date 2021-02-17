namespace PulsingServer

open System

[<Measure>]
type ms

type private ExternalInfoProviderMessage<'info> =
    | Get of AsyncReplyChannel<unit> option * int<ms>
    | NewConsumers of AsyncReplyChannel<unit> * ServerAgent<'info> array

/// An entity which periodically pulls information from some external source
/// and pushes it out to a collection of ServerAgents.
type ExternalInfoProvider<'info> =
    private
    | ExternalInfoProvider of MailboxProcessor<ExternalInfoProviderMessage<'info>>

[<RequireQualifiedAccess>]
module ExternalInfoProvider =

    /// Create an ExternalInfoProvider which runs the `get` async every `timer` milliseconds.
    /// When it gets a different `info`, it pings its `receivers` with that new info.
    /// The async returns when the ExternalInfoProvider has constructed its first info
    /// and has served that first info to its receivers.
    let make<'info when 'info : equality>
        (sleep : TimeSpan -> Async<unit>)
        (get : Async<'info>)
        (timer : int<ms>)
        (receivers : ServerAgent<'info> array)
        : Async<ExternalInfoProvider<'info>>
        =
        let rec loop (info : 'info option) (receivers : ServerAgent<'info> array) (mailbox : MailboxProcessor<ExternalInfoProviderMessage<'info>>) =
            async {
                match! mailbox.Receive () with
                | Get (channel, timeout) ->
                    let! newInfo = get
                    match info with
                    | Some info when newInfo = info ->
                        ()
                    | _ ->
                        do!
                            receivers
                            |> Array.map (ServerAgent.post newInfo)
                            |> Async.Parallel
                            |> Async.Ignore

                    match channel with
                    | None -> ()
                    | Some channel ->
                        channel.Reply ()
                    do! sleep (TimeSpan.FromMilliseconds (float timeout))
                    // There's a small inaccuracy here. We actually will wait until the end
                    // of a timeout cycle before we can process any new consumers. What we
                    // should really do is to allow NewConsumers messages to be processed
                    // during this downtime, by storing a "when did I start waiting" and
                    // testing "has `timeout` elapsed since then?", rather than just waiting
                    // for the timeout.
                    mailbox.Post (Get (None, timeout))
                    return! loop (Some newInfo) receivers mailbox
                | NewConsumers (channel, receivers) ->
                    channel.Reply ()
                    return! loop info receivers mailbox
            }

        async {
            let mailbox = MailboxProcessor.Start (loop None receivers)
            do! mailbox.PostAndAsyncReply (fun channel -> Get (Some channel, timer))
            return
                mailbox
                |> ExternalInfoProvider
        }

    /// Replace the collection of ServerAgents this ExternalInfoProvider is hooked up to.
    /// The replacement may take place any time after function invocation, but it is
    /// guaranteed to be complete once the Async returns.
    let updateConsumers<'info> (arr : ServerAgent<'info> array) (ExternalInfoProvider prov) : Async<unit> =
        prov.PostAndAsyncReply (fun channel -> NewConsumers (channel, arr))
