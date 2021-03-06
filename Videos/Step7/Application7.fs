namespace Step7.Application

module API =

  open Step7.Domain

  type Query =
    | Trucks
    | FlavoursInStock of Truck * Flavour


module Readmodels =
  open Step7.Infrastructure
  open Step7.Domain
  open API
    (*

      Was erwarten wir von einem Readmodel
      - spezifische Abfragen -> Query?
      - projections für einen Stream
      - projections für alle Streams


      Ablauf:

        - Wird gestartet
        - Holt sich Events aus Event Store
        - baut projection auf

        - Events kommen an
        - Readmodel entscheidet spezifisch wie es den state (für stream oder alle) holt (sql query, aus memory etc)
        - Readmodel schickt state an projection
        - projection generiert neuen State
        - Readmodel hinterlegt state

        - Query kommt an
        - Readmodel prüft, ob es damit umgehen kann
        - Readmodel entscheidet wie es damit umgeht
        - gibt antwort zurück

        Vorteil Memory: nichts anderes zu deployen etc
        Nachteil: Serverstart, Speicherverbrauch

        Zeigen: Behaviour kommt später, wir haben keinen Zugriff drauf

        Man kann mehr logik in ein Readmodel bauen oder unterschiedliche
    *)

  type Msg<'Event,'Query,'Result> =
    | Notify of EventEnvelope<'Event>
    | Query of 'Query * AsyncReplyChannel<QueryResult<'Result>>

  let flavoursInStock () =
    let agent =
      let initState : Map<EventSource, Map<Flavour, int>> = Map.empty  // hier record draus machen

      MailboxProcessor.Start(fun inbox ->
        let rec loop state =
          async {
            let! msg = inbox.Receive()

            match msg with
            | Notify eventEnvelope ->
                let newState =
                  state
                  |> Map.tryFind eventEnvelope.Source
                  |> Option.defaultValue Projections.flavoursInStock.Init
                  |> fun projectionState -> eventEnvelope.Event |> Projections.flavoursInStock.Update projectionState
                  |> fun newState -> state |> Map.add eventEnvelope.Source newState

                return! loop newState

            | Query (query, reply) ->
                let result =
                  match query with
                  | FlavoursInStock (Truck truck, flavour) ->
                      state
                      |> Map.tryFind truck
                      |> Option.defaultValue Map.empty
                      |> Map.tryFind flavour
                      |> Option.defaultValue 0
                      |> box
                      |> Handled

                   | _ ->
                      NotHandled

                result |> reply.Reply

                return! loop state
          }

        loop initState
      )

    {
      EventListener = Notify >> agent.Post
      QueryHandler = fun query -> agent.PostAndReply(fun reply -> Query (query,reply))
    }

  let trucks () =
    let agent =
      let initState = []

      MailboxProcessor.Start(fun inbox ->
        let rec loop state =
          async {
            let! msg = inbox.Receive()

            match msg with
            | Notify event ->
                return! loop state

            | Query (query, reply) ->
                let result =
                  match query with
                  | Trucks ->
                      [ System.Guid.NewGuid() |> Truck ]
                      |> box
                      |> Handled

                   | _ ->
                      NotHandled

                result |> reply.Reply

                return! loop state
          }

        loop initState
      )

    {
      EventListener = Notify >> agent.Post
      QueryHandler = fun query -> agent.PostAndReply(fun reply -> Query (query,reply))
    }