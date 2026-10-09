#pragma once
#include <array>
#include <cstddef>
#include <cstdint>

namespace gal {
struct Parameters {
    float micHighpassHz = 120.0f;
    float micLowpassHz = 11000.0f;
    float quietGainDb = 6.0f;
    float thresholdDbFs = -24.0f;
    float ratio = 10.0f;
    float kneeDb = 6.0f;
    float attackMs = 0.5f;
    float holdMs = 10.0f;
    float releaseMs = 150.0f;
    float ceiling = 0.98f;
    float wet = 0.0f;
    // ABI 2 character controls. Every default reproduces the ABI 1 output bit for bit.
    float lowShelfDb = 0.0f;
    float lowShelfHz = 200.0f;
    float presenceDb = 0.0f;
    float presenceHz = 3200.0f;
    float noiseDbFs = -120.0f;
    float saturation = 0.0f;
    float delayMs = 0.0f;
    // ABI 3. 1 keeps the first-order microphone band edges (6 dB/oct, ABI 1
    // arithmetic); 2 replaces them with Butterworth sections (12 dB/oct).
    float bandOrder = 1.0f;
};

// Output stage: exact clamp at saturation 0, otherwise linear to
// ceiling * (1 - saturation) and a tanh knee that never reaches the ceiling.
float limit(float value, float ceiling, float saturation) noexcept;

class Processor final {
public:
    static constexpr float NoiseOffDbFs = -120.0f;
    static constexpr float MaxDelayMs = 8.0f;

    Processor() noexcept;
    void reset(float sampleRate, float initialGainDb = 6.0f) noexcept;
    void process(const float* input, float* output, unsigned frames, int inChannels,
                 int outChannels, const Parameters& parameters) noexcept;
    float gainReductionDb() const noexcept { return reductionDb_; }
private:
    static constexpr int MaxChannels = 8;
    // 8 ms at 192 kHz, rounded up to a power of two for index masking.
    static constexpr int DelayCapacity = 2048;
    struct FilterState { float hpInput=0, hpOutput=0, low=0; };
    struct BiquadState { double x1=0, x2=0, y1=0, y2=0; };
    struct Biquad { double b0=1, b1=0, b2=0, a1=0, a2=0; bool active=false; };
    static double runBiquad(const Biquad& f, BiquadState& s, double x) noexcept;
    std::array<FilterState, MaxChannels> filters_{};
    std::array<BiquadState, MaxChannels> shelf_{};
    std::array<BiquadState, MaxChannels> presence_{};
    std::array<BiquadState, MaxChannels> bandHigh_{};
    std::array<BiquadState, MaxChannels> bandLow_{};
    std::array<std::uint32_t, MaxChannels> noise_{};
    std::array<std::array<float, DelayCapacity>, MaxChannels> delay_{};
    int delayWrite_ = 0;
    float sampleRate_ = 48000.0f;
    float gainDb_ = 6.0f;
    float reductionDb_ = 0.0f;
    int holdFrames_ = 0;
};

// Per-channel xorshift32 seeds; the managed mirror uses the same sequence.
std::uint32_t noiseSeed(int channel) noexcept;
}
