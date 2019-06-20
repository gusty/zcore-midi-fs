﻿
namespace ZMidi.Internal

module ParserMonad = 

    open System.IO

    open ZMidi.Internal.Utils

    /// Status is either OFF of the previous VoiceEvent * Channel.
    type VoiceEvent = 
        | StatusOff
        | NoteOn            of byte
        | NoteOff           of byte
        | NoteAftertoucuh   of byte
        | Control           of byte
        | Program           of byte
        | ChannelAftertouch of byte
        | PitchBend         of byte

  
    type MidiData = byte array

    type Pos = int

    type ErrMsg = 
      | EOF of where: string
      | Other of error: string
    type State = 
        { Position: Pos 
          RunningStatus: VoiceEvent
        }
    

    type ParserMonad<'a> = 
        private ParserMonad of (MidiData -> State -> Result<'a * State, ErrMsg> )

    let inline private apply1 (parser : ParserMonad<'a>) 
                       (midiData : byte[])
                       (state : State)  :  Result<'a * State, ErrMsg> = 
        let (ParserMonad fn) = parser in fn midiData state

    let inline mreturn (x:'a) : ParserMonad<'a> = 
        ParserMonad <| fun _ st -> Ok (x, st)

    let inline private bindM (parser : ParserMonad<'a>) 
                      (next : 'a -> ParserMonad<'b>) : ParserMonad<'b> = 
        ParserMonad <| fun input state -> 
            match apply1 parser input state with
            | Error msg -> Error msg
            | Ok (ans, st1) -> apply1 (next ans) input st1

    let mzero () : ParserMonad<'a> = 
        ParserMonad <| fun _ _ -> Error  (EOF "mzero")

    let inline mplus (parser1 : ParserMonad<'a>) (parser2 : ParserMonad<'a>) : ParserMonad<'a> = 
        ParserMonad <| fun input state -> 
            match apply1 parser1 input state with
            | Error _ -> apply1 parser2 input state
            | Ok res -> Ok res

    let inline private delayM (fn:unit -> ParserMonad<'a>) : ParserMonad<'a> = 
        bindM (mreturn ()) fn 


    type ParserBuilder() = 
        member self.Return x    = mreturn x
        member self.Bind (p,f)  = bindM p f
        member self.Zero ()     = mzero ()
        member self.Combine (ma, mb) = mplus ma mb
        member self.ReturnFrom (ma:ParserMonad<'a>) : ParserMonad<'a> = ma

    let (parseMidi:ParserBuilder) = new ParserBuilder()

    /// Run the parser on a file.
    let runParseMidi (ma : ParserMonad<'a>) (inputPath : string) : Result<'a, ErrMsg> = 
        use stream  = File.Open(path = inputPath, mode = FileMode.Open, access = FileAccess.Read)
        use memory = new MemoryStream()
        stream.CopyTo(memory)
        let input : byte [] = memory.ToArray()
        match apply1 ma input { Position = 0; RunningStatus = StatusOff} with
        | Ok (ans, _) -> Ok ans
        | Error msg -> Error msg

    /// Throw a parse error
    let parseError (genMessage : Pos -> string) : ParserMonad<'a> = 
        ParserMonad <| fun _ st -> Error (Other (genMessage st.Position))

    /// Run the parser, if it fails swap the error message.
    let ( <??> ) (parser : ParserMonad<'a>) (genMessage : Pos -> string) : ParserMonad<'a> = 
        ParserMonad <| fun input st -> 
            match apply1 parser input st with
            | Ok result -> Ok result
            | Error _ -> Error (Other (genMessage st.Position))


    let getRunningEvent : ParserMonad<VoiceEvent> = 
        ParserMonad <| fun _ st -> Ok (st.RunningStatus , st)

    let setRunningEvent (runningStatus : VoiceEvent) : ParserMonad<unit> = 
        ParserMonad <| fun _ st -> Ok ((),  { st with RunningStatus = runningStatus })

    let getPos : ParserMonad<int> =
        ParserMonad <| fun _ st -> Ok (st.Position, st)

    let inline private (|PositionValid|PositionInvalid|) (input: MidiData, state: State) =
      if state.Position >= 0 && state.Position < input.Length then
        PositionValid
      else
        PositionInvalid

    let inline private checkedParseM (name: string) (f: MidiData -> State -> Result<('a * State), ErrMsg>) =
      ParserMonad 
          (fun input state -> 
              try 
                match input,state with
                | PositionValid -> f input state
                | PositionInvalid -> Error (EOF name)
              with
              | e -> Error (Other (sprintf "%A" e))
          )

    let peek : ParserMonad<byte> =
        checkedParseM "peek" <|
            fun input st -> Ok (input.[st.Position], st)

    /// Conditionally gets a byte (word8). Fails if input is finished.
    /// Consumes data on if predicate succeeds, does not consume if
    /// predicate fails.
    let cond (test : byte -> bool) : ParserMonad<byte option> = 
        checkedParseM "cond" <|
            fun input st ->
                let a1 = input.[st.Position]
                if test a1 then 
                  Ok (Some a1, st) 
                else Ok (None, st)

    /// Repeats a given <see paramref="parser"/> <see paramref="length"/> times.
    /// Fails with accumulated errors when any encountered.
    let count (length : int) (parser : ParserMonad<'a>) : ParserMonad<'a []> =
        ParserMonad <| fun input state -> 
            let rec work (i : int) 
                         (st : State) 
                         (fk : ErrMsg -> Result<'a list * State, ErrMsg>) 
                         (sk : State -> 'a list  -> Result<'a list * State, ErrMsg>) = 
                if i <= 0 then 
                    sk st []
                else 
                    match apply1 parser input st with
                    | Error msg -> fk msg
                    | Ok (a1, st1) -> 
                        work (i-1) st1 fk (fun st2 ac -> 
                        sk st2 (a1 :: ac))
            work length state (fun msg -> Error msg) (fun st ac -> Ok (ac, st)) 
                |> Result.map (fun (ans, st) -> (List.toArray ans, st))

    /// Drop a byte (word8)
    let dropByte : ParserMonad<unit> = 
        checkedParseM "dropByte" <| 
            fun input st -> Ok ((), { st with Position = st.Position + 1 })

    /// Parse a byte (Word8).
    let readByte : ParserMonad<byte>= 
        checkedParseM "dropByte" <| 
            fun input st ->
                let a1 = input.[st.Position]
                Ok (a1, { st with Position = st.Position + 1 })
              
    /// Parse a single byte char.
    let readChar : ParserMonad<char> = 
        parseMidi { 
            let! a = readByte
            return (char a)
        }

    /// Parse a string of the given length.
    let readString (length : int) : ParserMonad<string> = 
        parseMidi { 
            let! arr = count length readChar
            return (System.String arr)
        }
        <??> sprintf "readString failed at %i"


    // Parse a uint16 (big endian).
    let readUint16be : ParserMonad<uint16>= 
        parseMidi { 
            let! a = readByte
            let! b = readByte
            return uint16be a b
            }
        <??> sprintf "uint16be: failed at %i"

    let readWord14be = 
        parseMidi {
            let! a = readByte
            let! b = readByte
            return (word14be a b)
            }
        <??> sprintf "word14be: failed at %i"