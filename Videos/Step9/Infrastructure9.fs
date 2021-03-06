namespace Step9.Infrastructure

type EventSource = System.Guid

type EventProducer<'Event> =
  'Event list -> 'Event list


type EventEnvelope<'Event> =
  {
    Source : EventSource
    Event : 'Event
  }

type EventListener<'Event> =
  EventEnvelope<'Event> -> unit

type EventResult<'Event> =
  Result<EventEnvelope<'Event> list, string>

type EventStore<'Event> =
  {
    Get : unit -> EventResult<'Event>
    GetStream : EventSource -> EventResult<'Event>
    Append : EventEnvelope<'Event> list -> unit
    Subscribe : EventListener<'Event>-> unit
    OnError : IEvent<exn>
  }

type EventStorage<'Event> =
  {
    Get : unit -> EventResult<'Event>
    GetStream : EventSource -> EventResult<'Event>
    Append : EventEnvelope<'Event> list -> unit
  }

type Projection<'State,'Event> =
  {
    Init : 'State
    Update : 'State -> 'Event -> 'State
  }

type QueryResult<'Result> =
  | Handled of obj
  | NotHandled
  | QueryError of string

type  QueryHandler<'Query,'Result> =
  {
    Handle : 'Query -> Async<QueryResult<'Result>>
  }

type ReadModel<'Event, 'State> =
  {
    EventListener : EventListener<'Event>
    State : unit -> Async<'State>
  }

type CommandHandler<'Command> =
  {
    Handle : EventSource -> 'Command -> unit
    OnError : IEvent<exn>
  }

type Behaviour<'Command,'Event> =
  'Command -> EventProducer<'Event>


/// A wrapper for MailboxProcessor that catches all unhandled exceptions
/// and reports them via the 'OnError' event. Otherwise, the API
/// is the same as the API of 'MailboxProcessor'
type Agent<'T>(f:Agent<'T> -> Async<unit>) as self =
  // Create an event for reporting errors
  let errorEvent = Event<_>()
  // Start standard MailboxProcessor
  let inbox = new MailboxProcessor<'T>(fun _ ->
    async {
      // Run the user-provided function & handle exceptions
      try return! f self
      with e -> errorEvent.Trigger(e)
    })

  /// Triggered when an unhandled exception occurs
  member __.OnError = errorEvent.Publish

  member __.Trigger exn = errorEvent.Trigger exn

  /// Starts the mailbox processor
  member __.Start() = inbox.Start()
  /// Receive a message from the mailbox processor
  member __.Receive() = inbox.Receive()
  /// Post a message to the mailbox processor
  member __.Post(value:'T) = inbox.Post value

  member __.PostAndReply(f: AsyncReplyChannel<'a> -> 'T) = inbox.PostAndReply f

  member __.PostAndAsyncReply(f: AsyncReplyChannel<'a> -> 'T) = inbox.PostAndAsyncReply f

  /// Start the mailbox processor
  static member Start f =
    let agent = new Agent<_>(f)
    agent.Start()
    agent


type EventSourced<'Comand,'Event,'Query,'Result>
  (eventStoreInit : EventStorage<'Event> -> EventStore<'Event>,
   eventStorageInit : unit -> EventStorage<'Event>,
   commandHandlerInit : EventStore<'Event> -> CommandHandler<'Comand>,
   queryHandler : QueryHandler<'Query,'Result>,
   eventListener : EventListener<'Event> list) =

  let eventStorage = eventStorageInit()

  let eventStore = eventStoreInit eventStorage

  let commandHandler = commandHandlerInit eventStore

  let queryHandler = queryHandler

  do
    eventStore.OnError.Add(printfn "eventStore Error: %A")
    commandHandler.OnError.Add(printfn "commandHandler Error: %A")

    eventListener
    |> List.iter (eventStore.Subscribe)

  member __.HandleCommand eventSource command =
    commandHandler.Handle eventSource command

  member __.HandleQuery query =
    queryHandler.Handle query

  member __.GetAllEvents () =
    eventStore.Get()

  member __.GetStream eventSource =
    eventStore.GetStream eventSource


module EventStorage =
  type Msg<'Event> =
    private
    | Get of AsyncReplyChannel<EventResult<'Event>>
    | GetStream of EventSource * AsyncReplyChannel<EventResult<'Event>>
    | Append of EventEnvelope<'Event> list


  module InMemory =

    let private streamFor source history =
      history
      |> List.filter (fun ee -> ee.Source = source)

    let initialize () : EventStorage<'Event> =
      let history : EventEnvelope<'Event> list = []

      let agent =
        Agent<Msg<_>>.Start(fun inbox ->
          let rec loop history =
            async {
              let! msg = inbox.Receive()

              match msg with
              | Get reply ->
                  history
                  |> Ok
                  |> reply.Reply

                  return! loop history

              | GetStream (source,reply) ->
                  history
                  |> streamFor source
                  |> Ok
                  |> reply.Reply

                  return! loop history

              | Append events ->
                  return! loop (history @ events)
            }

          loop history
        )

      {
        Get = fun () ->  agent.PostAndReply Get
        GetStream = fun eventSource -> agent.PostAndReply (fun reply -> (eventSource,reply) |> GetStream)
        Append = Append >> agent.Post
      }

  module FileStorage =

    open System.IO
    open Thoth.Json.Net

    let private writeEvents store events =
      use streamWriter = new StreamWriter(store, true)
      events
      |> List.map (fun eventEnvelope -> Encode.Auto.toString(0,eventEnvelope))
      |> List.iter streamWriter.WriteLine

      do streamWriter.Flush()

    let private get store =
      store
      |> File.ReadLines
      |> Seq.traverseResult Decode.Auto.fromString<EventEnvelope<'Event>>
      |> Result.map List.ofSeq

    let private getStream store source =
      store
      |> File.ReadLines
      |> Seq.traverseResult Decode.Auto.fromString<EventEnvelope<'Event>>
      |> Result.map (Seq.filter (fun ee -> ee.Source = source) >> List.ofSeq)

    let private append store events =
      use streamWriter = new StreamWriter(store, true)
      events
      |> List.map (fun eventEnvelope -> Encode.Auto.toString(0,eventEnvelope))
      |> List.iter streamWriter.WriteLine

      do streamWriter.Flush()


    let initialize store : EventStorage<_> =
      {
        Get = fun () -> get store
        GetStream = getStream store
        Append = append store
      }


module EventStore =

  type Msg<'Event> =
    | Get of AsyncReplyChannel<EventResult<'Event>>
    | GetStream of EventSource * AsyncReplyChannel<EventResult<'Event>>
    | Append of EventEnvelope<'Event> list
    | Subscribe of EventListener<'Event>

  let notifyEventListeners events subscriptions =
    subscriptions
    |> List.iter (fun subscription -> events |> List.iter subscription)

  let initialize (storage : EventStorage<_>) : EventStore<_> =

    let agent =
      Agent<Msg<_>>.Start(fun inbox ->
        let rec loop (eventListeners : EventListener<'Event> list) =
          async {
            let! msg = inbox.Receive()


            match msg with
            | Get reply ->
                try
                  storage.Get()
                  |> reply.Reply

                with exn ->
                  inbox.Trigger(exn)
                  exn.Message |> Error |> reply.Reply

                return! loop eventListeners


            | GetStream (source,reply) ->
                try
                  source
                  |> storage.GetStream
                  |> reply.Reply

                with exn ->
                  inbox.Trigger(exn)
                  exn.Message |> Error |> reply.Reply

                return! loop eventListeners

            | Append events ->
                try
                  do events |> storage.Append
                  do eventListeners |> notifyEventListeners events

                with exn ->
                  inbox.Trigger(exn)

                return! loop eventListeners

            | Subscribe listener ->
                try
                  do
                    storage.Get()
                    |> function | Ok result -> result |> List.iter listener | _ -> ()

                with exn ->
                  inbox.Trigger(exn)

                return! loop (listener :: eventListeners)
          }

        loop []
      )

    let getStream eventSource =
      agent.PostAndReply (fun reply -> (eventSource,reply) |> GetStream)

    let append events =
      events
      |> Append
      |> agent.Post

    let subscribe (subscription : EventListener<_>) =
      subscription
      |> Subscribe
      |> agent.Post

    {
      Get = fun () ->  agent.PostAndReply Get
      GetStream = getStream
      Append = append
      Subscribe = subscribe
      OnError = agent.OnError
    }


module CommandHandler =

  let private asEvents eventEnvelopes =
    eventEnvelopes
    |> List.map (fun envelope -> envelope.Event)

  let private enveloped source events =
    events
    |> List.map (fun event -> { Source = source ; Event = event })

  type Msg<'Command> =
    | Handle of 'Command

  let initialize (behaviour : Behaviour<_,_>) (eventStore : EventStore<_>) : CommandHandler<_> =
    let agent =
      Agent<Msg<_>>.Start(fun inbox ->
        let rec loop () =
          async {
            let! msg = inbox.Receive()

            match msg with
            | Handle (eventSource,command) ->
                eventSource
                |> eventStore.GetStream
                |> Result.map (asEvents >> behaviour command >> enveloped eventSource)
                |> function | Ok events -> events |> eventStore.Append | _ -> ()

                return! loop ()
          }

        loop ()
      )

    {
      Handle = fun source command -> (source,command) |> Handle |> agent.Post
      OnError = agent.OnError
    }

module QueryHandler =
  let rec private choice (queryHandler : QueryHandler<_,_> list) query =
    async {
      match queryHandler with
      | handler :: rest ->
          match! handler.Handle query with
          | NotHandled ->
              return! choice rest query

          | Handled response ->
              return Handled response

          | QueryError response ->
              return QueryError response

      | _ -> return NotHandled
    }

  let initialize queryHandlers : QueryHandler<_,_> =
   {
      Handle = choice queryHandlers
   }

  (*
    Event Storage -> Fehler zu EventStore. Wie damit umgehen? Wenn Exception dann? Ist selber MB, also dürfen wir hier auch nicht
    verschlucken

    Command Handler kaputt -> ich bin kaputt

    EventHandler kaputt -> ich bin kaputt




    TODO:
      * Checken was passiert, wenn mit Agent passiert, wenn der einen Agent aufruft und hier eine Exception auftritt -> geht kaputt
      * MBs abbrechen oder nicht bei Exception? -> nein
      * Wie mit Results umgehen (serialize,deserialize), soll hier eine Exception geworfen werden oder nicht? -> nein
      * Idee: Ich kann auf das OnError der EventStorage im EventStore hören -> eventStorage ist nicht mehr MB
      * Idee: Result + Error Event für Exception -> erledigt
      * Idee: EventStorage nicht als Agent -> erledigt

      * Auf Query Result Async hören
      * QueryHandler müssen ein Result zurückgeben


  *)

