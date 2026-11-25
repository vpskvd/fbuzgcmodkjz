use std::mem::ManuallyDrop;
use windows::Win32::Media::MediaFoundation::*;
use windows::Win32::System::Com::{
    CoInitializeEx, CoTaskMemFree, CoUninitialize, COINIT_MULTITHREADED,
};

pub const AAC_SAMPLES_PER_FRAME: u32 = 1024;

pub struct EncodedAac {
    pub sample_rate: u32,
    pub channels: u32,
    pub avg_bitrate: u32,
    pub max_bitrate: u32,
    pub audio_specific_config: Vec<u8>,
    pub frames: Vec<Vec<u8>>,
}

struct ComMfGuard {
    com_inited: bool,
    mf_started: bool,
}

impl ComMfGuard {
    fn init() -> Result<Self, i32> {
        let com_hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
        let com_inited = com_hr.is_ok();
        // S_OK / S_FALSE both need balancing CoUninitialize; RPC_E_CHANGED_MODE does not.
        unsafe { MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET) }.map_err(|e| {
            if com_inited {
                unsafe { CoUninitialize() };
            }
            e.code().0
        })?;
        Ok(Self {
            com_inited,
            mf_started: true,
        })
    }
}

impl Drop for ComMfGuard {
    fn drop(&mut self) {
        if self.mf_started {
            unsafe {
                let _ = MFShutdown();
            }
        }
        if self.com_inited {
            unsafe {
                CoUninitialize();
            }
        }
    }
}

pub fn default_audio_specific_config(rate: u32, channels: u32) -> Vec<u8> {
    let freq_idx: u16 = match rate {
        96_000 => 0,
        88_200 => 1,
        64_000 => 2,
        48_000 => 3,
        44_100 => 4,
        32_000 => 5,
        24_000 => 6,
        22_050 => 7,
        16_000 => 8,
        12_000 => 9,
        11_025 => 10,
        8_000 => 11,
        _ => 3,
    };
    let ch: u16 = channels.clamp(1, 2) as u16;
    // AudioObjectType = 2 (AAC-LC): 5 bits objectType | 4 bits freqIndex | 4 bits channels | 3 bits 0
    let val: u16 = (2 << 11) | (freq_idx << 7) | (ch << 3);
    vec![(val >> 8) as u8, (val & 0xFF) as u8]
}

fn activate_aac_mft() -> Result<IMFTransform, i32> {
    let out_info = MFT_REGISTER_TYPE_INFO {
        guidMajorType: MFMediaType_Audio,
        guidSubtype: MFAudioFormat_AAC,
    };
    let mut raw_ptr: *mut Option<IMFActivate> = std::ptr::null_mut();
    let mut count = 0u32;
    unsafe {
        MFTEnumEx(
            MFT_CATEGORY_AUDIO_ENCODER,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            None,
            Some(&out_info),
            &mut raw_ptr,
            &mut count,
        )
        .map_err(|e| e.code().0)?;
    }
    if raw_ptr.is_null() || count == 0 {
        if !raw_ptr.is_null() {
            unsafe { CoTaskMemFree(Some(raw_ptr as *const _)) };
        }
        return Err(-1);
    }

    let activates = unsafe { std::slice::from_raw_parts_mut(raw_ptr, count as usize) };
    let mut chosen: Result<IMFTransform, i32> = Err(-1);
    for slot in activates.iter_mut() {
        if let Some(act) = slot.take() {
            if chosen.is_err() {
                if let Ok(mft) = unsafe { act.ActivateObject::<IMFTransform>() } {
                    chosen = Ok(mft);
                }
            }
        }
    }
    unsafe {
        CoTaskMemFree(Some(raw_ptr as *const _));
    }
    chosen
}

fn drain_outputs(
    mft: &IMFTransform,
    provides_samples: bool,
    out_buf_size: u32,
    frames: &mut Vec<Vec<u8>>,
    max_frame_bytes: &mut usize,
) -> Result<(), i32> {
    loop {
        let owned_sample: Option<IMFSample> = if provides_samples {
            None
        } else {
            let s = unsafe { MFCreateSample().map_err(|e| e.code().0)? };
            let b = unsafe { MFCreateMemoryBuffer(out_buf_size).map_err(|e| e.code().0)? };
            unsafe { s.AddBuffer(&b).map_err(|e| e.code().0)? };
            Some(s)
        };

        let mut out_bufs = [MFT_OUTPUT_DATA_BUFFER {
            dwStreamID: 0,
            pSample: ManuallyDrop::new(owned_sample),
            dwStatus: 0,
            pEvents: ManuallyDrop::new(None),
        }];
        let mut status = 0u32;
        let hr = unsafe { mft.ProcessOutput(0, &mut out_bufs, &mut status) };

        let got_sample = unsafe { ManuallyDrop::take(&mut out_bufs[0].pSample) };
        let _got_events = unsafe { ManuallyDrop::take(&mut out_bufs[0].pEvents) };

        match hr {
            Ok(()) => {
                if let Some(sample) = got_sample {
                    if let Ok(buf) = unsafe { sample.ConvertToContiguousBuffer() } {
                        let mut ptr: *mut u8 = std::ptr::null_mut();
                        let mut len = 0u32;
                        unsafe {
                            if buf.Lock(&mut ptr, None, Some(&mut len)).is_ok() {
                                if len > 0 && !ptr.is_null() {
                                    let slice = std::slice::from_raw_parts(ptr, len as usize);
                                    *max_frame_bytes = (*max_frame_bytes).max(slice.len());
                                    frames.push(slice.to_vec());
                                }
                                let _ = buf.Unlock();
                            }
                        }
                    }
                }
            }
            Err(e) if e.code() == MF_E_TRANSFORM_NEED_MORE_INPUT => {
                return Ok(());
            }
            Err(e) if e.code() == MF_E_TRANSFORM_STREAM_CHANGE => {
                continue;
            }
            Err(e) => return Err(e.code().0),
        }
    }
}

pub fn encode_pcm(pcm: &[u8], sample_rate: u32, channels: u32) -> Result<EncodedAac, i32> {
    if sample_rate == 0 || channels == 0 || channels > 2 {
        return Err(-1);
    }
    let bpf = (channels * 2) as usize;
    if pcm.len() < bpf {
        return Err(-1);
    }

    let _guard = ComMfGuard::init()?;
    let mft = activate_aac_mft()?;

    let in_type = unsafe { MFCreateMediaType().map_err(|e| e.code().0)? };
    unsafe {
        in_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio).map_err(|e| e.code().0)?;
        in_type.SetGUID(&MF_MT_SUBTYPE, &MFAudioFormat_PCM).map_err(|e| e.code().0)?;
        in_type.SetUINT32(&MF_MT_AUDIO_BITS_PER_SAMPLE, 16).map_err(|e| e.code().0)?;
        in_type.SetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND, sample_rate).map_err(|e| e.code().0)?;
        in_type.SetUINT32(&MF_MT_AUDIO_NUM_CHANNELS, channels).map_err(|e| e.code().0)?;
        in_type.SetUINT32(&MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 2).map_err(|e| e.code().0)?;
        in_type
            .SetUINT32(&MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sample_rate * channels * 2)
            .map_err(|e| e.code().0)?;
    }

    // Prefer 192 kbps, fall back cleanly to 160 kbps / 128 kbps if rate limits apply.
    let mut configured = false;
    let mut chosen_bytes_per_sec = 24_000u32;
    for &bytes_per_sec in &[24_000u32, 20_000u32, 16_000u32] {
        let out_type = unsafe { MFCreateMediaType().map_err(|e| e.code().0)? };
        let ok = unsafe {
            out_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio).is_ok()
                && out_type.SetGUID(&MF_MT_SUBTYPE, &MFAudioFormat_AAC).is_ok()
                && out_type.SetUINT32(&MF_MT_AUDIO_BITS_PER_SAMPLE, 16).is_ok()
                && out_type.SetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND, sample_rate).is_ok()
                && out_type.SetUINT32(&MF_MT_AUDIO_NUM_CHANNELS, channels).is_ok()
                && out_type.SetUINT32(&MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytes_per_sec).is_ok()
                && out_type.SetUINT32(&MF_MT_AAC_PAYLOAD_TYPE, 0).is_ok()
                && out_type.SetUINT32(&MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29).is_ok()
                && mft.SetInputType(0, &in_type, 0).is_ok()
                && mft.SetOutputType(0, &out_type, 0).is_ok()
        };
        if ok {
            configured = true;
            chosen_bytes_per_sec = bytes_per_sec;
            break;
        }
    }
    if !configured {
        return Err(-2);
    }

    let asc = match unsafe { mft.GetOutputCurrentType(0) } {
        Ok(cur) => match unsafe { cur.GetBlobSize(&MF_MT_USER_DATA) } {
            Ok(sz) if sz >= 14 => {
                let mut blob = vec![0u8; sz as usize];
                if unsafe { cur.GetBlob(&MF_MT_USER_DATA, &mut blob, None) }.is_ok() {
                    // HEAACWAVEINFO header is 12 bytes followed by raw AudioSpecificConfig.
                    blob[12..].to_vec()
                } else {
                    default_audio_specific_config(sample_rate, channels)
                }
            }
            _ => default_audio_specific_config(sample_rate, channels),
        },
        Err(_) => default_audio_specific_config(sample_rate, channels),
    };

    let out_info = unsafe { mft.GetOutputStreamInfo(0).map_err(|e| e.code().0)? };
    let provides_samples = (out_info.dwFlags & 0x100) != 0;
    let out_buf_size = out_info.cbSize.max(8192);

    unsafe {
        mft.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0)
            .map_err(|e| e.code().0)?;
    }

    let mut frames = Vec::new();
    let mut max_frame_bytes = 0usize;
    let total_pcm_samples = pcm.len() / bpf;
    let chunk_bytes = 4096usize * bpf;
    let mut fed_samples = 0usize;

    for chunk in pcm[..total_pcm_samples * bpf].chunks(chunk_bytes) {
        let chunk_samples = chunk.len() / bpf;
        let sample = unsafe { MFCreateSample().map_err(|e| e.code().0)? };
        let buffer = unsafe { MFCreateMemoryBuffer(chunk.len() as u32).map_err(|e| e.code().0)? };
        unsafe {
            let mut ptr: *mut u8 = std::ptr::null_mut();
            buffer.Lock(&mut ptr, None, None).map_err(|e| e.code().0)?;
            std::ptr::copy_nonoverlapping(chunk.as_ptr(), ptr, chunk.len());
            buffer.Unlock().map_err(|e| e.code().0)?;
            buffer.SetCurrentLength(chunk.len() as u32).map_err(|e| e.code().0)?;
            sample.AddBuffer(&buffer).map_err(|e| e.code().0)?;
            let pts = (fed_samples as i64 * 10_000_000) / sample_rate as i64;
            let dur = (chunk_samples as i64 * 10_000_000) / sample_rate as i64;
            let _ = sample.SetSampleTime(pts);
            let _ = sample.SetSampleDuration(dur);
            mft.ProcessInput(0, &sample, 0).map_err(|e| e.code().0)?;
        }
        fed_samples += chunk_samples;
        drain_outputs(&mft, provides_samples, out_buf_size, &mut frames, &mut max_frame_bytes)?;
    }

    unsafe {
        let _ = mft.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        let _ = mft.ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    }
    drain_outputs(&mft, provides_samples, out_buf_size, &mut frames, &mut max_frame_bytes)?;

    if frames.is_empty() {
        return Err(-3);
    }

    let avg_bitrate = chosen_bytes_per_sec * 8;
    let peak_kbps = ((max_frame_bytes as u64 * 8 * sample_rate as u64) / AAC_SAMPLES_PER_FRAME as u64) as u32;
    let max_bitrate = peak_kbps.max(avg_bitrate);

    Ok(EncodedAac {
        sample_rate,
        channels,
        avg_bitrate,
        max_bitrate,
        audio_specific_config: asc,
        frames,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encodes_stereo_48k_pcm_to_raw_aac_frames() {
        let rate = 48_000u32;
        let ch = 2u32;
        let total_samples = rate as usize; // 1 second
        let mut pcm = Vec::with_capacity(total_samples * 4);
        for i in 0..total_samples {
            let phase = (i as f32 * 440.0 * std::f32::consts::TAU) / rate as f32;
            let val = (phase.sin() * 12000.0) as i16;
            let bytes = val.to_le_bytes();
            pcm.extend_from_slice(&bytes);
            pcm.extend_from_slice(&bytes);
        }

        let aac = encode_pcm(&pcm, rate, ch).expect("Windows MF AAC encoder should succeed");
        assert!(aac.frames.len() >= 45 && aac.frames.len() <= 52, "unexpected frame count: {}", aac.frames.len());
        assert!(aac.audio_specific_config.len() >= 2);
        // Raw AAC-LC must NOT start with ADTS syncword 0xFFFx.
        assert!(!(aac.frames[0][0] == 0xFF && (aac.frames[0][1] & 0xF0) == 0xF0));
        assert_eq!(aac.audio_specific_config[0..2], [0x11, 0x90]);
    }
}
