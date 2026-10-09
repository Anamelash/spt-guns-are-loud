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
constexpr double Pi = 3.14159265358979323846;
constexpr double PresenceQ = 1.2;
// RBJ cookbook, shelf slope S = 1.
void designLowShelf(double rate, double hz, double db, double& b0, double& b1, double& b2,
                    double& a1, double& a2) noexcept {
    const double A = std::pow(10.0, db / 40.0), w = 2 * Pi * hz / rate;
    const double c = std::cos(w), sq = 2 * std::sqrt(A) * (std::sin(w) / 2 * std::sqrt(2.0));
    const double a0 = (A + 1) + (A - 1) * c + sq;
    b0 = A * ((A + 1) - (A - 1) * c + sq) / a0;
    b1 = 2 * A * ((A - 1) - (A + 1) * c) / a0;
    b2 = A * ((A + 1) - (A - 1) * c - sq) / a0;
    a1 = -2 * ((A - 1) + (A + 1) * c) / a0;
    a2 = ((A + 1) + (A - 1) * c - sq) / a0;
}
void designPeak(double rate, double hz, double db, double& b0, double& b1, double& b2,
                double& a1, double& a2) noexcept {
    const double A = std::pow(10.0, db / 40.0), w = 2 * Pi * hz / rate;
    const double c = std::cos(w), alpha = std::sin(w) / (2 * PresenceQ);
    const double a0 = 1 + alpha / A;
    b0 = (1 + alpha * A) / a0; b1 = -2 * c / a0; b2 = (1 - alpha * A) / a0;
    a1 = -2 * c / a0; a2 = (1 - alpha / A) / a0;
}
// RBJ cookbook Butterworth sections (Q 1/sqrt 2): -3 dB at the corner, 12 dB/oct.
void designBandEdge(bool highpass, double rate, double hz, double& b0, double& b1, double& b2,
                    double& a1, double& a2) noexcept {
    const double w = 2 * Pi * hz / rate, c = std::cos(w), alpha = std::sin(w) / (2 * 0.70710678118654752);
    const double a0 = 1 + alpha;
    const double edge = highpass ? (1 + c) / 2 : (1 - c) / 2;
    b0 = edge / a0; b1 = (highpass ? -2 * edge : 2 * edge) / a0; b2 = edge / a0;
    a1 = -2 * c / a0; a2 = (1 - alpha) / a0;
}
float uniformNoise(std::uint32_t& state) noexcept {
    state ^= state << 13; state ^= state >> 17; state ^= state << 5;
    return static_cast<float>(static_cast<std::int32_t>(state)) * (1.0f / 2147483648.0f);
}
}

std::uint32_t noiseSeed(int channel) noexcept {
    const std::uint32_t seed = 0x9E3779B9u * static_cast<std::uint32_t>(channel + 1) + 0x7F4A7C15u;
    return seed ? seed : 1u;
}

float limit(float value, float ceiling, float saturation) noexcept {
    if (!(saturation > 0)) return clamp(value, -ceiling, ceiling);
    const float knee = ceiling * (1.0f - std::min(saturation, 1.0f));
    const float magnitude = std::abs(value);
    if (magnitude <= knee) return value;
    const float range = ceiling - knee;
    const float shaped = knee + range * std::tanh((magnitude - knee) / range);
    return value < 0 ? -std::min(shaped, ceiling) : std::min(shaped, ceiling);
}

Processor::Processor() noexcept { reset(48000.0f); }

double Processor::runBiquad(const Biquad& f, BiquadState& s, double x) noexcept {
    double y = f.b0 * x + f.b1 * s.x1 + f.b2 * s.x2 - f.a1 * s.y1 - f.a2 * s.y2;
    // Flush values that would decay into denormals instead of carrying them.
    y += 1.0e-20; y -= 1.0e-20;
    if (!std::isfinite(y)) { s = {}; return 0; }
    s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y;
    return y;
}

void Processor::reset(float sampleRate, float initialGainDb) noexcept {
    sampleRate_ = clamp(finiteOr(sampleRate, 48000), 8000, 192000);
    filters_ = {};
    shelf_ = {};
    presence_ = {};
    bandHigh_ = {};
    bandLow_ = {};
    for (int c = 0; c < MaxChannels; ++c) {
        noise_[c] = noiseSeed(c);
        delay_[c].fill(0.0f);
    }
    delayWrite_ = 0;
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
    // Band order 2 swaps the one-pole edges for Butterworth sections at the
    // same corners. The switch is a device change: the client resets with it.
    const bool steepBand = finiteOr(p.bandOrder, 1) >= 1.5f;
    Biquad bandHigh, bandLow;
    if (steepBand) {
        designBandEdge(true, sampleRate_, p.micHighpassHz, bandHigh.b0, bandHigh.b1, bandHigh.b2, bandHigh.a1, bandHigh.a2);
        designBandEdge(false, sampleRate_, std::min(p.micLowpassHz, sampleRate_ * 0.45f),
                       bandLow.b0, bandLow.b1, bandLow.b2, bandLow.a1, bandLow.a2);
    }
    const float attack = timeCoefficient(clamp(finiteOr(p.attackMs,.5f),0,1000), sampleRate_);
    const float release = timeCoefficient(clamp(finiteOr(p.releaseMs,150),0,5000), sampleRate_);
    const int holdMax = static_cast<int>(sampleRate_*clamp(finiteOr(p.holdMs,10),0,1000)*.001f);
    const float ceiling = clamp(finiteOr(p.ceiling,.98f), .01f, 1);
    const float quiet = clamp(finiteOr(p.quietGainDb,6),-24,24);
    // Character stage. A control at its default leaves its stage out entirely,
    // so the ABI 1 arithmetic is reproduced exactly.
    const float noiseDb = clamp(finiteOr(p.noiseDbFs, NoiseOffDbFs), NoiseOffDbFs, -30);
    const bool noiseOn = noiseDb > NoiseOffDbFs;
    const float noiseScale = noiseOn ? 1.7320508f * std::pow(10.0f, noiseDb / 20.0f) : 0.0f;
    Biquad shelf, peak;
    const float shelfDb = clamp(finiteOr(p.lowShelfDb, 0), -12, 0);
    if (shelfDb != 0) {
        const float hz = std::min(clamp(finiteOr(p.lowShelfHz, 200), 60, 500), sampleRate_ * 0.45f);
        designLowShelf(sampleRate_, hz, shelfDb, shelf.b0, shelf.b1, shelf.b2, shelf.a1, shelf.a2);
        shelf.active = true;
    }
    const float presenceDb = clamp(finiteOr(p.presenceDb, 0), 0, 12);
    if (presenceDb != 0) {
        const float hz = std::min(clamp(finiteOr(p.presenceHz, 3200), 1000, 6000), sampleRate_ * 0.45f);
        designPeak(sampleRate_, hz, presenceDb, peak.b0, peak.b1, peak.b2, peak.a1, peak.a2);
        peak.active = true;
    }
    const float saturation = clamp(finiteOr(p.saturation, 0), 0, 1);
    const int delayFrames = std::min(DelayCapacity - 1, static_cast<int>(std::lround(
        clamp(finiteOr(p.delayMs, 0), 0, MaxDelayMs) * 0.001 * sampleRate_)));
    if (!std::isfinite(gainDb_)) gainDb_ = quiet;
    for (unsigned f=0; f<frames; ++f) {
        std::array<float, MaxChannels> filtered{};
        float detector=0;
        const int filteredChannels=std::min(std::min(inChannels,outChannels),MaxChannels);
        for (int c=0;c<filteredChannels;++c) {
            const float dry=finiteOr(input[f*inChannels+c],0);
            auto& s=filters_[c];
            if (steepBand) {
                filtered[c] = static_cast<float>(runBiquad(bandLow, bandLow_[c], runBiquad(bandHigh, bandHigh_[c], dry)));
            } else {
                const float high=hpPole*(s.hpOutput+dry-s.hpInput);
                s.hpInput=dry; s.hpOutput=high;
                filtered[c]=(1-lpPole)*high+lpPole*s.low; s.low=filtered[c];
            }
            // Preamplifier self-noise enters before the gain and the detector.
            if (noiseOn) filtered[c] += noiseScale * uniformNoise(noise_[c]);
            if (shelf.active) filtered[c] = static_cast<float>(runBiquad(shelf, shelf_[c], filtered[c]));
            else { auto& b=shelf_[c]; b.x2=b.x1; b.x1=filtered[c]; b.y2=b.y1; b.y1=filtered[c]; }
            if (peak.active) filtered[c] = static_cast<float>(runBiquad(peak, presence_[c], filtered[c]));
            else { auto& b=presence_[c]; b.x2=b.x1; b.x1=filtered[c]; b.y2=b.y1; b.y1=filtered[c]; }
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
            float electronic=limit(finiteOr(microphone*gain,0),ceiling,saturation);
            if (c < MaxChannels) {
                delay_[c][delayWrite_] = electronic;
                if (delayFrames > 0) electronic = delay_[c][(delayWrite_ - delayFrames) & (DelayCapacity - 1)];
            }
            output[f*outChannels+c]=finiteOr(dry+wet*(electronic-dry),0);
        }
        delayWrite_ = (delayWrite_ + 1) & (DelayCapacity - 1);
    }
}
}
