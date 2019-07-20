namespace ZMidi

open ZMidi.DataTypes

module ReadFile =
    open ZMidi.Internal.ParserMonad
    open ZMidi.Internal.Utils
    open ZMidi.Internal.ExtraTypes

    /// Apply parse then apply the check, if the check fails report
    /// the error message. 
    let postCheck parser isOutputValid errorMessage =
      parseMidi {
        let! answer = parser
        if isOutputValid answer then 
          return answer 
        else 
          return! fatalError errorMessage
      }

    let inline (|TestBit|_|) (bit: int) (i: ^T) =
      let mask = LanguagePrimitives.GenericOne <<< bit
      if mask &&& i = mask then Some () else None

    let inline clearBit (bit: int) (i: ^T) =
      let mask = ~~~ (LanguagePrimitives.GenericOne <<< bit)
      i &&& mask
    let inline msbHigh i =
      match i with
      | TestBit 7 -> true
      | _ -> false
    let assertString (s: string) =
      postCheck (readString s.Length) ((=) s) (Other (sprintf "assertString: expected '%s'" s))

    let assertWord32 i =
      postCheck readUInt32be ((=) i) (Other (sprintf "assertWord32: expected '%i'" i))

    let assertWord8 i =
      postCheck readByte ((=) i) (Other (sprintf "assertWord8: expected '%i'" i))

    let getVarlen : ParserMonad<word32> =
      let rec loop acc =
        parseMidi {
          let! b = readByte
          let acc = acc <<< 7
          if msbHigh b then
            let result = uint64 (b &&& 0b01111111uy)
            return! loop (acc + result)
          else
            return (acc + (uint64 b)) }  
      parseMidi {
        let! result = loop 0UL
        return (uint32 result) }
      
    let getVarlenText = gencount getVarlen readChar (fun _ b -> System.String b)
    let getVarlenBytes = gencount getVarlen readByte (fun _ b -> b)
    let deltaTime = 
      parseMidi {
          let! v = getVarlen
          return DeltaTime(v)
      } <??> (fun p -> "delta time")
    
    let fileFormat =
        parseMidi {
            match! readUInt16be with
            | 0us -> return MidiFormat0
            | 1us -> return MidiFormat1
            | 2us -> return MidiFormat2
            | x   -> return! (fatalError (Other (sprintf "fileFormat: Unrecognized file format %i" x)))
            }
    let timeDivision =
        parseMidi {
          match! readUInt16be with
          | TestBit 15 as x -> return FramePerSecond (clearBit 15 x)
          | x               -> return TicksPerBeat x
          }
    let header = 
        parseMidi {
            let! _ = assertString "MThd"
            let! _ = assertWord32 6u 
            let! format = fileFormat
            let! trackCount = readUInt16be
            let! timeDivision = timeDivision
            return { trackCount = trackCount
                     timeDivision = timeDivision
                     format = format }
            }
    let trackHeader =
        parseMidi {
          let! _ = assertString "MTrk"
          return! readUInt32be
        }

    let textEvent textType =
      parseMidi {
        let! a = assertWord8 2uy
        let! b = peek
        let! text = getVarlenText
        return TextEvent(textType, text)
      }

    let metaEventSequenceNumber =
      parseMidi {
        let! a = assertWord8 2uy
        let! b = peek
        return SequenceNumber(word16be a b)
      }

    let metaEvent i =
      parseMidi {
        match i with
        | 0x00uy -> return! metaEventSequenceNumber
        | 0x01uy -> return! textEvent GenericText
        | 0x02uy -> return! textEvent CopyrightNotice
        | 0x03uy -> return! textEvent SequenceName
        | 0x04uy -> return! textEvent InstrumentName
        | 0x05uy -> return! textEvent Lyrics
        | 0x06uy -> return! textEvent Marker
        | 0x07uy -> return! textEvent CuePoint
      }
//    let (<*>) af ma =


    //let metaEvent n = //: ParserMonad<MetaEvent> =
    //  match n with
    //  ///| 0x00uy -> ( SequenceNumber <~> (assertWord8 2uy *> word16be)) <??> (sprintf "sequence number: failed at %i" )
    //  | 0x01uy -> (textEvent GenericText)     <??> (sprintf "generic text: failed at %i")
    //  | 0x02uy -> (textEvent CopyrightNotice) <??> (sprintf "generic text: failed at %i")
    //  | 0x03uy -> (textEvent SequenceName)    <??> (sprintf "generic text: failed at %i")
    //  | 0x04uy -> (textEvent InstrumentName)  <??> (sprintf "generic text: failed at %i")
    //  | 0x05uy -> (textEvent Lyrics)          <??> (sprintf "generic text: failed at %i")
    //  | 0x06uy -> (textEvent Marker)          <??> (sprintf "generic text: failed at %i")
    //  | 0x07uy -> (textEvent CuePoint)        <??> (sprintf "generic text: failed at %i")
    //  //| 0x20uy -> (textEvent GenericText)     <??> (sprintf "generic text: failed at %i")
    //  | _ -> failwithf "metaEvent %i" n

      //parseMidi {
      //  
      //}

//sysExContPackets = 
//  deltaTime >>= \dt -> getVarlenBytes (,) >>= \(n,xs) -> 
//  let ans1 = MidiSysExContPacket dt n xs
//  in if isTerminated xs then return [ans1]
//                        else sysExContPackets >>= \ks -> 
//                             return $ ans1:ks

    let isTerminated bytes =
      bytes 
      |> Array.tryFind ((=) 0xf7uy) 
      |> function | Some i -> true
                  | None -> false

    let rec sysExContPackets =
      parseMidi {
        let! d = deltaTime
        let! b = getVarlenBytes
        let answer = MidiSysExContPacket (d, b)
        if isTerminated b then return List.singleton answer
                          else 
                            let! answer2 = sysExContPackets
                            return (answer :: answer2)
      }
    let sysExEvent =
      parseMidi {
        let! b = getVarlenBytes
        if isTerminated b then
          return SysExSingle b
        else
          let! cont = sysExContPackets
          return SysExCont(b, cont)
      } <??> (fun _ -> "sysExEvent")
    let sysExEscape = 
      parseMidi {
        let! bytes = getVarlenBytes
        return SysExEscape bytes
      } <??> (fun _ -> "sysExEscape")
    let impossibleMatch text =
      fatalError (ErrMsg.Other (sprintf "impossible match: %s" text))

    let sysCommonEvent n =
      match n with
      | 0xf1uy -> readByte >>= (QuarterFrame >> mreturn) <??> (fun p -> "quarter frame")
      | 0xf2uy -> 
        parseMidi {
          let! a = readByte
          let! b = readByte
          return SongPosPointer(a,b)
        } <??> (fun p -> "song pos. pointer")
      | 0xf3uy -> readByte >>= (QuarterFrame >> mreturn) <??> (fun p -> "song select")
      | 0xf4uy -> mreturn UndefinedF4
      | 0xf5uy -> mreturn UndefinedF5
      | 0xf6uy -> mreturn TuneRequest
      | 0xf7uy -> mreturn EOX
      | tag -> impossibleMatch (sprintf "sysCommonEvent %x" tag)

    let sysRealtimeEvent n =
      match n with
      | 0xf8uy -> mreturn TimingClock
      | 0xf9uy -> mreturn TimingClock
      | 0xfauy -> mreturn TimingClock
      | 0xfbuy -> mreturn TimingClock
      | 0xfcuy -> mreturn TimingClock
      | 0xfduy -> mreturn TimingClock
      | 0xfeuy -> mreturn TimingClock
      | 0xffuy -> mreturn TimingClock
      | tag ->  impossibleMatch (sprintf "sysRealtimeEvent %x" tag)

    let inline (|SB|) b =
      b &&& 0xf0uy, b &&& 0x0fuy

    let noteOff ch =
      parseMidi {
        let! a = readByte
        let! b = readByte
        return MidiVoiceEvent.NoteOff(ch, a, b)
      } <??> (fun p -> "note-off")

    let noteOn ch =
      parseMidi {
        let! a = readByte
        let! b = readByte
        return MidiVoiceEvent.NoteOn(ch, a, b)
      } <??> (fun p -> "note-on")

    let noteAftertouch ch =
      parseMidi {
        let! a = readByte
        let! b = readByte
        return MidiVoiceEvent.NoteAfterTouch(ch, a, b)
      } <??> (fun p -> "noteAftertouch")

    let controller ch =
      parseMidi {
        let! a = readByte
        let! b = readByte
        return MidiVoiceEvent.Controller(ch, a, b)
      } <??> (fun p -> "controller")

    let programChange ch =
      parseMidi {
        let! a = readByte
        return MidiVoiceEvent.ProgramChange(ch, a)
      } <??> (fun p -> "controller")

    let channelAftertouch ch =
      parseMidi {
        let! a = readByte
        return MidiVoiceEvent.ChannelAftertouch(ch, a)
      } <??> (fun p -> "channelAftertouch")
    let pitchBend ch =
      parseMidi {
        let! a = readWord14be
        return MidiVoiceEvent.PitchBend(ch, a)
      } <??> (fun p -> "pitchBend")


    let voiceEvent n =
      match n with
      | SB(0x80uy, ch) -> parseMidi { do! setRunningEvent (NoteOff ch)
                                      return! noteOff ch }
      | SB(0x90uy, ch) -> parseMidi { do! setRunningEvent (NoteOn ch)
                                      return! noteOn ch }
      | SB(0xa0uy, ch) -> parseMidi { do! setRunningEvent (NoteAftertoucuh ch)
                                      return! noteAftertouch ch }
      | SB(0xb0uy, ch) -> parseMidi { do! setRunningEvent (Control ch)
                                      return! controller ch }
      | SB(0xc0uy, ch) -> parseMidi { do! setRunningEvent (Program ch)
                                      return! programChange ch }
      | SB(0xd0uy, ch) -> parseMidi { do! setRunningEvent (ChannelAftertouch ch)
                                      return! channelAftertouch ch }
      | SB(0xe0uy, ch) -> parseMidi { do! setRunningEvent (PitchBend ch)
                                      return! pitchBend ch }
      | otherwise -> impossibleMatch (sprintf "voiceEvent: %x" otherwise)
    let runningStatus (event: VoiceEvent) : ParserMonad<MidiEvent> = 
      let mVoiceEvent e = mreturn (VoiceEvent(MidiRunningStatus.ON, e))
      match event with
      | NoteOff           ch -> (noteOff ch)           >>= mVoiceEvent
      | NoteOn            ch -> (noteOn ch)            >>= mVoiceEvent
      | NoteAftertoucuh   ch -> (noteAftertouch ch)    >>= mVoiceEvent
      | Control           ch -> (controller ch)        >>= mVoiceEvent
      | Program           ch -> (programChange ch)     >>= mVoiceEvent
      | ChannelAftertouch ch -> (channelAftertouch ch) >>= mVoiceEvent
      | PitchBend         ch -> (pitchBend ch)         >>= mVoiceEvent
      | StatusOff            -> readByte >>= (MidiEventOther >> mreturn)
      //parseMidi {
      //
      //  //return MidiRunningStatus.ON
      //}
      
      (*
event :: ParserM MidiEvent
event = peek >>= step
  where
    -- 00..7f  -- /data/
    step n
      | n == 0xFF  = MetaEvent         <$> (dropW8 *> (word8 >>= metaEvent))
      | n >= 0xF8  = SysRealTimeEvent  <$> (dropW8 *> sysRealTimeEvent n)
      | n == 0xF7  = SysExEvent        <$> (dropW8 *> sysExEscape)
      | n >= 0xF1  = SysCommonEvent    <$> (dropW8 *> sysCommonEvent n)
      | n == 0xF0  = SysExEvent        <$> (dropW8 *> sysExEvent)
      | n >= 0x80  = VoiceEvent RS_OFF <$> (dropW8 *> voiceEvent (splitByte n))
      | otherwise  = getRunningEvent >>= runningStatus
      *)

    /// Parse an event - for valid input this function should parse
    /// without error (i.e all cases of event types are fully 
    /// enumerated). 
    ///
    /// Malformed input (syntactically bad events, or truncated data) 
    /// can cause fatal parse errors.
    
    let event : ParserMonad<MidiEvent> = 
      //let foo = (readByte >>= metaEvent)
      let step n : ParserMonad<MidiEvent>= 
        parseMidi {
          match n with
          | 0xffuy -> 
            do! dropByte
            let! event = readByte >>= metaEvent
            return MetaEvent event
            //MetaEvent <~> (dropByte *> (fun _ -> (readByte >>= metaEvent))) // failwithf "event ff"
          | 0xf7uy -> 
            do! dropByte
            let! sysexEvent = sysExEscape
            return SysExEvent sysexEvent
          | 0xf0uy ->
            do! dropByte
            let! sysexEvent = sysExEvent
            return SysExEvent sysexEvent
          | x when x >= 0xf8uy ->
            do! dropByte
            let! event = sysRealtimeEvent x
            return SysRealtimeEvent event
          | x when x >= 0xf1uy ->
            do! dropByte
            let! event = sysCommonEvent x
            return SysCommonEvent event
          | x when x >= 0x80uy ->
            do! dropByte
            let! voiceEvent = voiceEvent x
            return VoiceEvent(MidiRunningStatus.OFF, voiceEvent)
          | otherwise ->
            return! (getRunningEvent >>= runningStatus)

        }
      parseMidi {
        let! p = peek
        return! step p 
      }

    let message = 
        parseMidi {
          let! deltaTime = deltaTime
          let! event = event
          return { timestamp = deltaTime; event = event }
        }
    let messages i = 
      parseMidi {
          return! boundRepeat (int i) message
      }
    let track : ParserMonad<MidiTrack> =
        parseMidi {
            let! length = trackHeader
            return! messages length
        }

    let midiFile =
      parseMidi {
        let! header = header
        let! tracks = count (header.trackCount) track
        return { header = header; tracks = tracks }
      }