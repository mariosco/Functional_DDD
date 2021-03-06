﻿module FDDD.CommandHandlers

open Domain

(*#################

    Commandhandler ist der infrastrukturelle Link zwischen Eventstore, Command und Aggregat.
    Ausser ID's auspacken muss hier nichts verändert werden.

#################*)

let warenkorb =
    function
    | Lege_Warenkorb_an { Warenkorb = Warenkorb id } -> id
    | Bestelle_Warenkorb { Warenkorb = Warenkorb id } -> id

// The discard pile command handler is the link
// between the command, the event store and the aggregate
// This version loads the aggregate from scratch for each command
// This is usually ok for aggregates with a small number of events
module Commandhandler =


    let create readStream appendToStream =

        // this is the "repository"
        let streamId warenkorb = sprintf "Warenkorb-%d" warenkorb
        let load warenkorb =
            let rec fold state version =
                let events, lastEvent, nextEvent = readStream (streamId warenkorb) version 500
                let state = List.fold evolve state events
                match nextEvent with
                | None -> lastEvent, state
                | Some n -> fold state n
            fold State.initial 0

        let save warenkorb expectedVersion events = appendToStream (streamId warenkorb) expectedVersion events

        // the mapsnd function works on a pair.
        // It applies the function on the second element.
        let inline mapsnd f (v,s) = v, f s

        fun command ->
            let id = warenkorb command

            load id
            |> mapsnd (handle command)
            ||> save id


// This version use F# agents (MailboxProcessor) to keep
// aggregates in memory.
// The dispatcher agent maintains a map of existing agents/aggregates,
// and forward commands to it.
// The agent loads aggregate state when receiving its first command,
// then simply maintain state and save new events to the store.
// This is usually ok for aggregates with a small number of events
module Async =
    module Game =

        type Agent<'T> = MailboxProcessor<'T>
        let create readStream appendToStream =

            // this is the "repository"
            let streamId warenkorb = sprintf "Warenkorb-%d" warenkorb
            let load warenkorb =
                let rec fold state version =
                    async {
                    let! events, lastEvent, nextEvent =
                        readStream (streamId warenkorb) version 500

                    let state = List.fold evolve state events
                    match nextEvent with
                    | None -> return lastEvent, state
                    | Some n -> return! fold state n }
                fold State.initial 0

            let save warenkorb expectedVersion events =
                appendToStream (streamId warenkorb) expectedVersion events

            let start warenkorb =
                Agent.Start
                <| fun inbox ->
                    let rec loop version state =
                        async {
                            let! command = inbox.Receive()
                            let events = handle command state
                            do! save warenkorb version events

                            let newState = List.fold evolve state events
                            return! loop (version + List.length events) newState  }
                    async {
                        let! version, state = load warenkorb
                        return! loop version state }
            let forward (agent: Agent<_>) command = agent.Post command

            let dispatcher =
                Agent.Start
                <| fun inbox ->
                    let rec loop aggregates =
                        async {
                            let! command = inbox.Receive()
                            let id = warenkorb command
                            match Map.tryFind id aggregates with
                            | Some aggregate ->
                                forward aggregate command
                                return! loop aggregates
                            | None ->
                                let aggregate = start id
                                forward aggregate command
                                return! loop (Map.add id aggregate aggregates) }
                    loop Map.empty

            fun command ->
                dispatcher.Post command
