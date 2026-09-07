#include "GalHeadphoneDsp.h"
#include <algorithm>
#include <cmath>
#include <cstring>

namespace gal {
namespace {
float finiteOr(float v, float fallback) noexcept { return std::isfinite(v) ? v : fallback; }
float clamp(float v, float lo, float hi) noexcept { return std::max(lo, std::min(hi, v)); }
float timeCoefficient(float ms, float rate) noexcept {
    return ms <= 0 ? 0.0f : std::exp(-1.0f / (rate * ms * 0.001f));
}
float staticGain(float levelDb, const Parameters& p) noexcept {
    const float ratio = clamp(finiteOr(p.ratio, 10), 1, 100);
    const float knee = clamp(finiteOr(p.kneeDb, 6), 0, 48);
    const float over = levelDb - clamp(finiteOr(p.thresholdDbFs, -24), -96, 0);
    const float slope = 1.0f - 1.0f / ratio;
    float reduction = 0;
    if (knee <= 0) reduction = over > 0 ? over * slope : 0;
    else if (over >= knee * 0.5f) reduction = over * slope;
    else if (over > -knee * 0.5f) {
        const float x = over + knee * 0.5f;
        reduction = slope * x * x / (2 * knee);
    }
    return clamp(finiteOr(p.quietGainDb, 6), -24, 24) - reduction;
}
}

void Processor::reset(float sampleRate, float initialGainDb) noexcept {
    sampleRate_ = clamp(finiteOr(sampleRate, 48000), 8000, 192000);
    filters_ = {};
    gainDb_ = clamp(finiteOr(initialGainDb, 6), -24, 24);
    reductionDb_ = 0;
    holdFrames_ = 0;
}

void Processor::process(const float* input, float* output, unsigned frames, int inChannels,
                        int outChannels, const Parameters& raw) noexcept {
    if (!output || frames == 0 || outChannels <= 0) return;
    const float wet = clamp(finiteOr(raw.wet, 0), 0, 1);
    const unsigned samples = frames * static_cast<unsigned>(outChannels);
    if (!input || inChannels <= 0) { std::fill(output, output + samples, 0); return; }
    if (wet <= 0) {
        for (unsigned f=0; f<frames; ++f) for (int c=0; c<outChannels; ++c)
            output[f*outChannels+c] = c < inChannels ? input[f*inChannels+c] : 0;
        return;
    }
    Parameters p = raw;
    p.micHighpassHz = clamp(finiteOr(p.micHighpassHz,120), 10, sampleRate_*0.44f);
    p.micLowpassHz = clamp(finiteOr(p.micLowpassHz,11000), p.micHighpassHz, sampleRate_*0.49f);
    const float hpPole = std::exp(-6.28318530718f*p.micHighpassHz/sampleRate_);
    const float lpPole = std::exp(-6.28318530718f*p.micLowpassHz/sampleRate_);
    const float attack = timeCoefficient(clamp(finiteOr(p.attackMs,.5f),0,1000), sampleRate_);
    const float release = timeCoefficient(clamp(finiteOr(p.releaseMs,150),0,5000), sampleRate_);
    const int holdMax = static_cast<int>(sampleRate_*clamp(finiteOr(p.holdMs,10),0,1000)*.001f);
    const float ceiling = clamp(finiteOr(p.ceiling,.98f), .01f, 1);
    const float quiet = clamp(finiteOr(p.quietGainDb,6),-24,24);
    if (!std::isfinite(gainDb_)) gainDb_ = quiet;
    for (unsigned f=0; f<frames; ++f) {
        std::array<float, MaxChannels> filtered{};
        float detector=0;
        const int filteredChannels=std::min(std::min(inChannels,outChannels),MaxChannels);
        for (int c=0;c<filteredChannels;++c) {
            const float dry=finiteOr(input[f*inChannels+c],0);
            auto& s=filters_[c];
            const float high=hpPole*(s.hpOutput+dry-s.hpInput);
            s.hpInput=dry; s.hpOutput=high;
            filtered[c]=(1-lpPole)*high+lpPole*s.low; s.low=filtered[c];
            if(c<2) detector=std::max(detector,std::abs(filtered[c]));
        }
        const float levelDb=20*std::log10(std::max(detector,1.0e-7f));
        const float target=staticGain(levelDb,p);
        if (target < gainDb_) { gainDb_=attack*gainDb_+(1-attack)*target; holdFrames_=holdMax; }
        else if (holdFrames_>0) --holdFrames_;
        else gainDb_=release*gainDb_+(1-release)*target;
        reductionDb_=std::max(0.0f,quiet-gainDb_);
        const float gain=std::pow(10.0f,gainDb_/20.0f);
        for (int c=0;c<outChannels;++c) {
            const float dry=c<inChannels?finiteOr(input[f*inChannels+c],0):0;
            const float microphone=c<filteredChannels?filtered[c]:0;
            const float electronic=clamp(finiteOr(microphone*gain,0),-ceiling,ceiling);
            output[f*outChannels+c]=finiteOr(dry+wet*(electronic-dry),0);
        }
    }
}
}
