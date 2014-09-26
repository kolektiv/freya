﻿namespace Dyfrig.Machine

open System
open Aether
open Aether.Operators
open Dyfrig.Core
open Dyfrig.Core.Operators
open Dyfrig.Http
open Dyfrig.Pipeline


[<AutoOpen>]
module Definition =

    type MachineAction = 
        OwinMonad<unit>

    type MachineDecision = 
        OwinMonad<bool>

    type MachineHandler = 
        OwinMonad<byte []>

    type MachineOperation =
        OwinMonad<unit>

    type MachineDefinition =
        Map<string, MachineOverride>

    and MachineOverride =
        | Action of MachineAction
        | Configuration of obj
        | Decision of MachineDecision
        | Handler of MachineHandler


[<AutoOpen>]
module Monad =

    type MachineMonad = 
        MachineDefinition -> unit * MachineDefinition

    type MachineMonadBuilder () =

        member __.Return _ : MachineMonad =
            fun definition -> (), definition

        member __.ReturnFrom machine : MachineMonad = 
            machine

        member __.Bind (m, k) : MachineMonad = 
            m >> fun (result, definition) -> (k result) definition

        member x.Combine (m1, m2) : MachineMonad = 
            x.Bind (m1, fun () -> m2)

        member internal x.Set (r, lens, value) = 
            x.Bind ((fun res -> (), setPL lens value res), fun _ -> x.ReturnFrom r)

    let machine = MachineMonadBuilder ()


[<AutoOpen>]
module Cache =
    
    let cache<'T> m =
        let lens =
                 dictPLens (string (Guid.NewGuid ()))
            <?-> boxIso<'T>

        owin {
            let! value = getPLM lens

            match value with
            | Some cached ->
                return cached
            | _ ->
                let! created = m
                do! setPLM lens created

                return created }


[<AutoOpen>]
module internal Lenses =

    let actionPLens k =
             mapPLens k
        <??> ((function | Action x -> Some x | _ -> None), 
              (fun x -> Action x))

    
    let configurationPLens<'T> k =
             mapPLens k
        <??> ((function | Configuration x -> Some x | _ -> None), 
              (fun x -> Configuration x))
        <?-> boxIso<'T>
        
    let decisionPLens k =
             mapPLens k
        <??> ((function | Decision x -> Some x | _ -> None), 
              (fun x -> Decision x))

    let handlerPLens k =
             mapPLens k
        <??> ((function | Handler x -> Some x | _ -> None), 
              (fun x -> Handler x))

    let definitionLens =
             dictLens "dyfrig.machine.definition"
        <--> boxIso<MachineDefinition>


[<AutoOpen>]
module internal Actions =

    let defaultAction =
        owin { return () }


[<AutoOpen>]
module internal Configuration =

    let defaultAllowedMethods =
        Set.ofList
            [ GET
              HEAD ]

    let defaultKnownMethods =
        Set.ofList 
            [ DELETE
              HEAD
              GET
              OPTIONS
              PATCH
              POST
              PUT
              TRACE ]


[<AutoOpen>]
module internal Decisions =

    let defaultDecision (decision: bool) = 
        owin { return decision }


[<AutoOpen>]
module internal Handlers =

    let defaultHandler =
        owin { return Array.empty<byte> }


[<AutoOpen>]
module internal Operations =

    let defaultOperation statusCode reasonPhrase =
           setPLM Response.statusCode statusCode
        *> setPLM Response.reasonPhrase reasonPhrase

    // TODO: Make this inspect available methods, origins, etc. properly.
    // TODO: Break allowed origin in to an earlier operation
    let defaultOptions =
           setPLM Response.statusCode 200
        *> setPLM Response.reasonPhrase "Options"
        *> setPLM (Response.header "Access-Control-Allow-Origin") [| "*" |]
        *> setPLM (Response.header "Access-Control-Allow-Headers") [| "Content-Type" |]


[<AutoOpen>]
module internal Logic =

    let equals l value =
        (=) value <!> getLM l

    let equalsP l value =
        (=) (Some value) <!> getPLM l

    let existsP l =
        Option.isSome <!> getPLM l

    let validP l f =
        (function | Some x -> f x | _ -> false) <!> getPLM l


    [<AutoOpen>]
    module Conditional =

        let ifMatchExists =
            existsP Request.Headers.ifMatch

        let ifNoneMatchExists =
            existsP Request.Headers.ifNoneMatch

        let ifModifiedSinceExists =
            existsP Request.Headers.ifModifiedSince

        let ifUnmodifiedSinceExists =
            existsP Request.Headers.ifUnmodifiedSince

        let ifMatchStar =
            equalsP Request.Headers.ifMatch IfMatch.Any

        let ifNoneMatchStar =
            equalsP Request.Headers.ifNoneMatch IfNoneMatch.Any

        let ifModifiedSinceValidDate =
            validP Request.Headers.ifModifiedSince ((<) DateTime.UtcNow)

        let ifUnmodifiedSinceValidDate =
            validP Request.Headers.ifUnmodifiedSince ((<) DateTime.UtcNow)

        let ifNoneMatch =
            defaultDecision true // IMPLEMENT

        let ifETagMatchesIf =
            defaultDecision true // IMPLEMENT

        let ifETagMatchesIfNone =
            defaultDecision true // IMPLEMENT

        let ifModifiedSince =
            defaultDecision true // IMPLEMENT
            
        let ifUnmodifiedSince =
            defaultDecision true // IMPLEMENT


    [<AutoOpen>]
    module Method =

        let private getMethods k d =
            (fun x -> defaultArg x d) <!> getPLM (definitionLens >-?> configurationPLens<Set<Method>> k)

        let private isValidMethod key defaults =
            owin {
                let! m = getLM Request.meth
                let! ms = getMethods key defaults

                return Set.contains m ms }

        let ifMethodAllowed =
            isValidMethod Config.AllowedMethods defaultAllowedMethods

        let ifMethodKnown =
            isValidMethod Config.KnownMethods defaultKnownMethods

        let ifMethodDelete =
            equals Request.meth DELETE

        let ifMethodOptions =
            equals Request.meth OPTIONS

        let ifMethodPatch =
            equals Request.meth PATCH

        let ifMethodPost =
            equals Request.meth POST

        let ifMethodPut =
            equals Request.meth PUT


    [<AutoOpen>]
    module Negotiation =

        let ifAcceptExists =
            existsP Request.Headers.accept

        let ifAcceptCharsetExists =
            existsP Request.Headers.acceptCharset

        let ifAcceptEncodingExists =
            existsP Request.Headers.acceptEncoding

        let ifAcceptLanguageExists =
            existsP Request.Headers.acceptLanguage

        let ifCharsetAvailable =
            defaultDecision true // IMPLEMENT

        let ifEncodingAvailable =
            defaultDecision true // IMPLEMENT

        let ifLanguageAvailable =
            defaultDecision true // IMPLEMENT

        let ifMediaTypeAvailable =
            defaultDecision true // IMPLEMENT


[<AutoOpen>]
module internal Execution =

    type Graph =
        Map<string, Node>

    and Node =
        | Action of ActionNode
        | Decision of DecisionNode
        | Handler of HandlerNode
        | Operation of OperationNode
    
    and ActionNode =
        { Metadata: Metadata
          Override: Override
          Action: MachineAction
          Next: string }
    and DecisionNode =
        { Metadata: Metadata
          Override: Override
          Decision: MachineDecision
          True: string
          False: string }

    and HandlerNode =
        { Metadata: Metadata
          Override: Override
          Handler: MachineHandler }

    and OperationNode =
        { Metadata: Metadata
          Operation: MachineOperation
          Next: string }

    and Metadata =
        { Name: string
          Description: string option }

    and Override =
        { AllowOverride: bool
          Overridden: bool }

    let private nodes =
        [ 

          // Actions

          Action { Metadata =
                     { Name = Actions.Delete
                       Description = None }
                   Override =
                     { AllowOverride = true
                       Overridden = false }
                   Action = defaultAction
                   Next = Decisions.Deleted }

          Action { Metadata =
                     { Name = Actions.Patch
                       Description = None }
                   Override =
                     { AllowOverride = true
                       Overridden = false }
                   Action = defaultAction
                   Next = Decisions.RespondWithEntity }

          Action { Metadata =
                     { Name = Actions.Post
                       Description = None }
                   Override =
                     { AllowOverride = true
                       Overridden = false }
                   Action = defaultAction
                   Next = Decisions.PostRedirect }

          Action { Metadata =
                     { Name = Actions.Put
                       Description = None }
                   Override =
                     { AllowOverride = true
                       Overridden = false }
                   Action = defaultAction
                   Next = Decisions.Created }

          // Decisions (Allow Override = false)

          Decision { Metadata =
                       { Name = Decisions.AcceptCharsetExists
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifAcceptCharsetExists
                     True = Decisions.CharsetAvailable
                     False = Decisions.AcceptEncodingExists }

          Decision { Metadata =
                       { Name = Decisions.AcceptEncodingExists
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifAcceptEncodingExists
                     True = Decisions.EncodingAvailable
                     False = Decisions.Processable }

          Decision { Metadata =
                       { Name = Decisions.AcceptExists 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifAcceptExists
                     True = Decisions.MediaTypeAvailable
                     False = Decisions.AcceptLanguageExists }

          Decision { Metadata =
                       { Name = Decisions.AcceptLanguageExists 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifAcceptLanguageExists
                     True = Decisions.LanguageAvailable
                     False = Decisions.AcceptCharsetExists }

          Decision { Metadata =
                       { Name = Decisions.IfMatchExists 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMatchExists
                     True = Decisions.IfMatchStar
                     False = Decisions.IfUnmodifiedSinceExists }

          Decision { Metadata =
                       { Name = Decisions.IfMatchStar 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMatchStar
                     True = Decisions.IfUnmodifiedSinceExists
                     False = Decisions.ETagMatchesIf }

          Decision { Metadata =
                       { Name = Decisions.IfMatchStarExistsForMissing 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMatchExists
                     True = Operations.PrePreconditionFailed
                     False = Decisions.MethodPut }

          Decision { Metadata =
                       { Name = Decisions.IfModifiedSinceExists 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifModifiedSinceExists
                     True = Decisions.IfModifiedSinceValidDate
                     False = Decisions.MethodDelete }

          Decision { Metadata =
                       { Name = Decisions.IfModifiedSinceValidDate 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifModifiedSinceValidDate
                     True = Decisions.ModifiedSince
                     False = Decisions.MethodDelete }

          Decision { Metadata =
                       { Name = Decisions.IfNoneMatch 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifNoneMatch
                     True = Operations.PreNotModified
                     False = Operations.PrePreconditionFailed }

          Decision { Metadata =
                       { Name = Decisions.IfNoneMatchExists 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifNoneMatchExists
                     True = Decisions.IfNoneMatchStar
                     False = Decisions.IfModifiedSinceExists }

          Decision { Metadata =
                       { Name = Decisions.IfNoneMatchStar 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifNoneMatchStar
                     True = Decisions.IfNoneMatch
                     False = Decisions.ETagMatchesIfNone }

          Decision { Metadata =
                       { Name = Decisions.IfUnmodifiedSinceExists 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifUnmodifiedSinceExists
                     True = Decisions.IfUnmodifiedSinceValidDate
                     False = Decisions.IfNoneMatchExists }

          Decision { Metadata =
                       { Name = Decisions.IfUnmodifiedSinceValidDate 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifUnmodifiedSinceValidDate
                     True = Decisions.UnmodifiedSince
                     False = Decisions.IfNoneMatchExists }

          Decision { Metadata =
                       { Name = Decisions.MethodDelete 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMethodDelete
                     True = Actions.Delete
                     False = Decisions.MethodPatch }

          Decision { Metadata =
                       { Name = Decisions.MethodOptions 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMethodOptions
                     True = Operations.PreOptions
                     False = Decisions.AcceptExists }

          Decision { Metadata =
                       { Name = Decisions.MethodPatch 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMethodPatch
                     True = Actions.Patch
                     False = Decisions.PostToExisting }

          Decision { Metadata =
                       { Name = Decisions.MethodPut 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMethodPut
                     True = Decisions.PutToDifferentUri
                     False = Decisions.Existed }

          Decision { Metadata =
                       { Name = Decisions.PostToGone 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMethodPost
                     True = Decisions.CanPostToGone
                     False = Operations.PreGone }

          Decision { Metadata =
                       { Name = Decisions.PostToExisting 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMethodPost
                     True = Actions.Post
                     False = Decisions.PutToExisting }

          Decision { Metadata =
                       { Name = Decisions.PostToMissing 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMethodPost
                     True = Decisions.CanPostToMissing
                     False = Operations.PreNotFound }

          Decision { Metadata =
                       { Name = Decisions.PutToExisting 
                         Description = None }
                     Override =
                       { AllowOverride = false
                         Overridden = false }
                     Decision = ifMethodPost
                     True = Decisions.Conflict
                     False = Decisions.MultipleRepresentations }

          // Decisions (Allow Override = true)

          Decision { Metadata =
                       { Name = Decisions.Allowed
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.ContentTypeValid
                     False = Operations.PreForbidden }
                      
          Decision { Metadata =
                       { Name = Decisions.Authorized
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.Allowed
                     False = Operations.PreUnauthorized }
                      
          Decision { Metadata =
                       { Name = Decisions.CanPostToGone
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Actions.Post
                     False = Operations.PreGone }
                      
          Decision { Metadata =
                       { Name = Decisions.CanPostToMissing
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Actions.Post
                     False = Operations.PreNotFound }
                      
          Decision { Metadata =
                       { Name = Decisions.CanPutToMissing
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.Conflict
                     False = Operations.PreNotImplemented }
                      
          Decision { Metadata =
                       { Name = Decisions.CharsetAvailable
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifCharsetAvailable
                     True = Decisions.AcceptEncodingExists
                     False = Operations.PreNotAcceptable }
                      
          Decision { Metadata =
                       { Name = Decisions.Conflict
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Operations.PreConflict
                     False = Actions.Put }
                      
          Decision { Metadata =
                       { Name = Decisions.ContentTypeKnown
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.ValidEntityLength
                     False = Operations.PreUnsupportedMediaType }
                      
          Decision { Metadata =
                       { Name = Decisions.ContentTypeValid
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.ContentTypeKnown
                     False = Operations.PreNotImplemented }
                      
          Decision { Metadata =
                       { Name = Decisions.Created
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Operations.PreCreated
                     False = Decisions.RespondWithEntity }
                      
          Decision { Metadata =
                       { Name = Decisions.Deleted
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.RespondWithEntity
                     False = Operations.PreAccepted }
                      
          Decision { Metadata =
                       { Name = Decisions.EncodingAvailable
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifEncodingAvailable
                     True = Decisions.Processable
                     False = Operations.PreNotAcceptable }
                      
          Decision { Metadata =
                       { Name = Decisions.ETagMatchesIf
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifETagMatchesIf
                     True = Decisions.IfUnmodifiedSinceExists
                     False = Operations.PrePreconditionFailed }
                      
          Decision { Metadata =
                       { Name = Decisions.ETagMatchesIfNone
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifETagMatchesIfNone
                     True = Decisions.IfNoneMatch
                     False = Decisions.IfModifiedSinceExists }
                      
          Decision { Metadata =
                       { Name = Decisions.Existed
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Decisions.MovedPermanently
                     False = Decisions.PostToMissing }
                      
          Decision { Metadata =
                       { Name = Decisions.Exists
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.IfMatchExists
                     False = Decisions.IfMatchStarExistsForMissing }
                      
          Decision { Metadata =
                       { Name = Decisions.MethodKnown
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifMethodKnown
                     True = Decisions.UriTooLong
                     False = Operations.PreUnknownMethod }
                      
          Decision { Metadata =
                       { Name = Decisions.LanguageAvailable
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifLanguageAvailable
                     True = Decisions.AcceptCharsetExists
                     False = Operations.PreNotAcceptable }
                      
          Decision { Metadata =
                       { Name = Decisions.Malformed
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Operations.PreMalformed
                     False = Decisions.Authorized }
                      
          Decision { Metadata =
                       { Name = Decisions.MediaTypeAvailable
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifMediaTypeAvailable
                     True = Decisions.AcceptLanguageExists
                     False = Operations.PreNotAcceptable }
                      
          Decision { Metadata =
                       { Name = Decisions.MethodAllowed
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifMethodAllowed
                     True = Decisions.Malformed
                     False = Operations.PreMethodNotAllowed }
                      
          Decision { Metadata =
                       { Name = Decisions.ModifiedSince
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifModifiedSince
                     True = Decisions.MethodDelete
                     False = Operations.PreNotModified }
                      
          Decision { Metadata =
                       { Name = Decisions.MovedPermanently
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Operations.PreMovedPermanently
                     False = Decisions.MovedTemporarily }
                      
          Decision { Metadata =
                       { Name = Decisions.MovedTemporarily
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Operations.PreMovedTemporarily
                     False = Decisions.PostToGone }
                      
          Decision { Metadata =
                       { Name = Decisions.MultipleRepresentations
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Operations.PreMultipleRepresentations
                     False = Operations.PreOK }
                      
          Decision { Metadata =
                       { Name = Decisions.PostRedirect
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Operations.PreSeeOther
                     False = Decisions.Created }
                      
          Decision { Metadata =
                       { Name = Decisions.Processable
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.Exists
                     False = Operations.PreUnprocessableEntity }
                      
          Decision { Metadata =
                       { Name = Decisions.PutToDifferentUri
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Operations.PreMovedPermanently
                     False = Decisions.CanPutToMissing }
                      
          Decision { Metadata =
                       { Name = Decisions.RespondWithEntity
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.MultipleRepresentations
                     False = Operations.PreNoContent }
                      
          Decision { Metadata =
                       { Name = Decisions.ServiceAvailable
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.MethodKnown
                     False = Operations.PreServiceUnavailable }
                      
          Decision { Metadata =
                       { Name = Decisions.UnmodifiedSince
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = ifUnmodifiedSince
                     True = Operations.PrePreconditionFailed
                     False = Decisions.IfNoneMatchExists }
                      
          Decision { Metadata =
                       { Name = Decisions.UriTooLong
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision false
                     True = Operations.PreUriTooLong
                     False = Decisions.MethodAllowed }

          Decision { Metadata =
                       { Name = Decisions.ValidEntityLength
                         Description = None }
                     Override =
                       { AllowOverride = true
                         Overridden = false }
                     Decision = defaultDecision true
                     True = Decisions.MethodOptions
                     False = Operations.PreRequestEntityTooLarge }

          // Handlers
                     
          Handler { Metadata =
                      { Name = Handlers.OK
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }
                    
          Handler { Metadata =
                      { Name = Handlers.Options
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.Created
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.Accepted
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.NoContent
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.MovedPermanently
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.SeeOther
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler  }

          Handler { Metadata =
                      { Name = Handlers.NotModified
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.MovedTemporarily
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.MultipleRepresentations
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.Malformed
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.Unauthorized
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.Forbidden
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.NotFound
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.MethodNotAllowed
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.NotAcceptable
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.Conflict
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.Gone
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.PreconditionFailed
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.RequestEntityTooLarge
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.UriTooLong
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.UnsupportedMediaType
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.UnprocessableEntity
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.Exception
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.NotImplemented
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.UnknownMethod
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }

          Handler { Metadata =
                      { Name = Handlers.ServiceUnavailable
                        Description = None }
                    Override =
                      { AllowOverride = true
                        Overridden = false }
                    Handler = defaultHandler }
                    
          // Operations
          
          Operation { Metadata =
                        { Name = Operations.PreOK
                          Description = None }
                      Operation = defaultOperation 200 "OK"
                      Next = Handlers.OK }
                    
          Operation { Metadata =
                       { Name = Operations.PreOptions
                         Description = None }
                      Operation = defaultOptions
                      Next = Handlers.Options }

          Operation { Metadata =
                       { Name = Operations.PreCreated
                         Description = None }
                      Operation = defaultOperation 201 "Created"
                      Next = Handlers.Created }

          Operation { Metadata =
                       { Name = Operations.PreAccepted
                         Description = None }
                      Operation = defaultOperation 202 "Accepted"
                      Next = Handlers.Accepted }

          Operation { Metadata =
                       { Name = Operations.PreNoContent
                         Description = None }
                      Operation = defaultOperation 204 "No Content"
                      Next = Handlers.NoContent }

          Operation { Metadata =
                       { Name = Operations.PreMovedPermanently
                         Description = None }
                      Operation = defaultOperation 301 "Moved Permanently"
                      Next = Operations.PreMovedPermanently }

          Operation { Metadata =
                       { Name = Operations.PreSeeOther
                         Description = None }
                      Operation = defaultOperation 303 "See Other"
                      Next = Handlers.SeeOther }

          Operation { Metadata =
                       { Name = Operations.PreNotModified
                         Description = None }
                      Operation = defaultOperation 304 "Not Modified"
                      Next = Handlers.NotModified }

          Operation { Metadata =
                       { Name = Operations.PreMovedTemporarily
                         Description = None }
                      Operation = defaultOperation 307 "Moved Temporarily"
                      Next = Handlers.MovedTemporarily }

          Operation { Metadata =
                       { Name = Operations.PreMultipleRepresentations
                         Description = None }
                      Operation = defaultOperation 310 "Multiple Representations"
                      Next = Handlers.MultipleRepresentations }

          Operation { Metadata =
                       { Name = Operations.PreMalformed
                         Description = None }
                      Operation = defaultOperation 400 "Bad Request"
                      Next = Handlers.Malformed }

          Operation { Metadata =
                       { Name = Operations.PreUnauthorized
                         Description = None }
                      Operation = defaultOperation 401 "Unauthorized"
                      Next = Handlers.Unauthorized }

          Operation { Metadata =
                       { Name = Operations.PreForbidden
                         Description = None }
                      Operation = defaultOperation 403 "Forbidden"
                      Next = Handlers.Forbidden }

          Operation { Metadata =
                       { Name = Operations.PreNotFound
                         Description = None }
                      Operation = defaultOperation 404 "Not Found"
                      Next = Handlers.NotFound }

          Operation { Metadata =
                       { Name = Operations.PreMethodNotAllowed
                         Description = None }
                      Operation = defaultOperation 405 "Method Not Allowed"
                      Next = Handlers.MethodNotAllowed }

          Operation { Metadata =
                       { Name = Operations.PreNotAcceptable
                         Description = None }
                      Operation = defaultOperation 406 "Not Acceptable"
                      Next = Handlers.NotAcceptable }

          Operation { Metadata =
                       { Name = Operations.PreConflict
                         Description = None }
                      Operation = defaultOperation 409 "Conflict"
                      Next = Handlers.Conflict }

          Operation { Metadata =
                       { Name = Operations.PreGone
                         Description = None }
                      Operation = defaultOperation 410 "Gone"
                      Next = Handlers.Gone }

          Operation { Metadata =
                       { Name = Operations.PrePreconditionFailed
                         Description = None }
                      Operation = defaultOperation 412 "Precondition Failed"
                      Next = Handlers.PreconditionFailed }

          Operation { Metadata =
                       { Name = Operations.PreRequestEntityTooLarge
                         Description = None }
                      Operation = defaultOperation 413 "Request Entity Too Large"
                      Next = Handlers.RequestEntityTooLarge }

          Operation { Metadata =
                       { Name = Operations.PreUriTooLong
                         Description = None }
                      Operation = defaultOperation 414 "URI Too Long"
                      Next = Handlers.UriTooLong }

          Operation { Metadata =
                       { Name = Operations.PreUnsupportedMediaType
                         Description = None }
                      Operation = defaultOperation 415 "Unsupported Media Type"
                      Next = Handlers.UnsupportedMediaType }

          Operation { Metadata =
                       { Name = Operations.PreUnprocessableEntity
                         Description = None }
                      Operation = defaultOperation 422 "Unprocessable Entity"
                      Next = Handlers.UnprocessableEntity }

          Operation { Metadata =
                       { Name = Operations.PreException
                         Description = None }
                      Operation = defaultOperation 500 "Internal Server Error"
                      Next = Handlers.Exception }

          Operation { Metadata =
                       { Name = Operations.PreNotImplemented
                         Description = None }
                      Operation = defaultOperation 501 "Not Implemented"
                      Next = Handlers.NotImplemented }

          Operation { Metadata =
                       { Name = Operations.PreUnknownMethod
                         Description = None }
                      Operation = defaultOperation 501 "Unknown Method"
                      Next = Handlers.UnknownMethod }

          Operation { Metadata =
                       { Name = Operations.PreServiceUnavailable
                         Description = None }
                      Operation = defaultOperation 503 "Service Unavailable"
                      Next = Handlers.ServiceUnavailable } ]

    let construct (definition: MachineDefinition) =
        nodes
        |> List.map (fun n ->
            match n with
            | Action x ->
                x.Metadata.Name,
                match x.Override.AllowOverride, getPL (actionPLens x.Metadata.Name) definition with
                | true, Some a -> Action { x with Action = a; Override = { x.Override with Overridden = true } }
                | _ -> n
            | Decision x -> 
                x.Metadata.Name,
                match x.Override.AllowOverride, getPL (decisionPLens x.Metadata.Name) definition with
                | true, Some d -> Decision { x with Decision = d; Override = { x.Override with Overridden = true } }
                | _ -> n
            | Handler x -> 
                x.Metadata.Name,
                match x.Override.AllowOverride, getPL (handlerPLens x.Metadata.Name) definition with
                | true, Some h -> Handler { x with Handler = h; Override = { x.Override with Overridden = true } }
                | _ -> n
            | Operation x ->
                x.Metadata.Name, n)
        |> Map.ofList

    let execute (graph: Graph) =
        let rec traverse from =
            owin {
                match Map.find from graph with
                | Action action ->
                    do! action.Action
                    printfn "action: %s" action.Metadata.Name
                    return! traverse action.Next
                | Decision decision ->
                    let! p = decision.Decision
                    let next = p |> function | true -> decision.True | _ -> decision.False
                    printfn "decision: %s = %b" from p
                    return! traverse next
                | Handler handler ->
                    printfn "handler: %s" handler.Metadata.Name
                    return! handler.Handler
                | Operation operation ->
                    do! operation.Operation
                    printfn "operation: %s" operation.Metadata.Name
                    return! traverse operation.Next }

        traverse Decisions.ServiceAvailable


[<AutoOpen>]
module Reification =
    
    let reifyMachine (machine: MachineMonad) : Pipeline =
        let definition = machine Map.empty |> snd
        let graph = construct definition

        owin {
            do! setLM definitionLens definition

            let! body = execute graph

            do! setPLM (Response.header "Content-Length") [| string body.Length |]
            do! modLM Response.body (fun x -> x.Write (body, 0, body.Length); x)
        
            return Halt }
