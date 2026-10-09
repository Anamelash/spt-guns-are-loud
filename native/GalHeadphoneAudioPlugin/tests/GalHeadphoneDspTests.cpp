#include "GalHeadphoneDsp.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <limits>
#include <memory>
#include <vector>

// Verbatim ABI 1 processor (pluginversion 0x00010001). With every ABI 2
// control at its default the current processor must match it bit for bit.
namespace galv1 {
struct Parameters {
    float micHighpassHz = 120.0f; float micLowpassHz = 11000.0f; float quietGainDb = 6.0f;
    float thresholdDbFs = -24.0f; float ratio = 10.0f; float kneeDb = 6.0f; float attackMs = 0.5f;
    float holdMs = 10.0f; float releaseMs = 150.0f; float ceiling = 0.98f; float wet = 0.0f;
};
float finiteOr(float v, float fallback) noexcept { return std::isfinite(v) ? v : fallback; }
float clamp(float v, float lo, float hi) noexcept { return std::max(lo, std::min(hi, v)); }
float timeCoefficient(float ms, float rate) noexcept { return ms <= 0 ? 0.0f : std::exp(-1.0f / (rate * ms * 0.001f)); }
float staticGain(float levelDb, const Parameters& p) noexcept {
    const float ratio = clamp(finiteOr(p.ratio, 10), 1, 100);
    const float knee = clamp(finiteOr(p.kneeDb, 6), 0, 48);
    const float over = levelDb - clamp(finiteOr(p.thresholdDbFs, -24), -96, 0);
    const float slope = 1.0f - 1.0f / ratio;
    float reduction = 0;
    if (knee <= 0) reduction = over > 0 ? over * slope : 0;
    else if (over >= knee * 0.5f) reduction = over * slope;
    else if (over > -knee * 0.5f) { const float x = over + knee * 0.5f; reduction = slope * x * x / (2 * knee); }
    return clamp(finiteOr(p.quietGainDb, 6), -24, 24) - reduction;
}
class Processor final {
public:
    void reset(float sampleRate, float initialGainDb = 6.0f) noexcept {
        sampleRate_ = clamp(finiteOr(sampleRate, 48000), 8000, 192000); filters_ = {};
        gainDb_ = clamp(finiteOr(initialGainDb, 6), -24, 24); reductionDb_ = 0; holdFrames_ = 0;
    }
    void process(const float* input, float* output, unsigned frames, int inChannels, int outChannels, const Parameters& raw) noexcept {
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
            std::array<float, MaxChannels> filtered{}; float detector=0;
            const int filteredChannels=std::min(std::min(inChannels,outChannels),MaxChannels);
            for (int c=0;c<filteredChannels;++c) {
                const float dry=finiteOr(input[f*inChannels+c],0); auto& s=filters_[c];
                const float high=hpPole*(s.hpOutput+dry-s.hpInput); s.hpInput=dry; s.hpOutput=high;
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
private:
    static constexpr int MaxChannels = 8;
    struct FilterState { float hpInput=0, hpOutput=0, low=0; };
    std::array<FilterState, MaxChannels> filters_{};
    float sampleRate_ = 48000.0f, gainDb_ = 6.0f, reductionDb_ = 0.0f; int holdFrames_ = 0;
};
}

static int fails=0; static void check(bool x,const char*m){if(!x){std::cerr<<"FAIL "<<m<<'\n';++fails;}}
static constexpr double Pi=3.14159265358979323846;

static std::vector<float> stereo(const char* name, int rate){
    std::vector<float> data(static_cast<size_t>(rate)*2,0.0f);
    if(!std::strcmp(name,"impulse")) data[0]=data[1]=1.0f;
    else if(!std::strcmp(name,"sine")) for(int f=0;f<rate;++f) data[f*2]=data[f*2+1]=static_cast<float>(0.1*std::sin(2*Pi*1000*f/rate));
    else if(!std::strcmp(name,"pink")) for(int c=0;c<2;++c){
        std::uint32_t s=c?0x7F4A7C15u:0x9E3779B9u; double b0=0,b1=0,b2=0,b3=0,b4=0,b5=0,b6=0;
        for(int f=0;f<rate;++f){ s^=s<<13; s^=s>>17; s^=s<<5; double w=s/4294967296.0*2-1;
            b0=0.99886*b0+w*0.0555179; b1=0.99332*b1+w*0.0750759; b2=0.96900*b2+w*0.1538520; b3=0.86650*b3+w*0.3104856;
            b4=0.55000*b4+w*0.5329522; b5=-0.7616*b5-w*0.0168980; double pk=b0+b1+b2+b3+b4+b5+b6+w*0.5362; b6=w*0.115926;
            data[f*2+c]=static_cast<float>(pk*0.0251); } }
    return data;
}
static galv1::Parameters v1(const gal::Parameters& p){
    galv1::Parameters o; o.micHighpassHz=p.micHighpassHz; o.micLowpassHz=p.micLowpassHz; o.quietGainDb=p.quietGainDb;
    o.thresholdDbFs=p.thresholdDbFs; o.ratio=p.ratio; o.kneeDb=p.kneeDb; o.attackMs=p.attackMs; o.holdMs=p.holdMs;
    o.releaseMs=p.releaseMs; o.ceiling=p.ceiling; o.wet=p.wet; return o;
}
static std::vector<float> render(const gal::Parameters& p,const std::vector<float>& in,int rate,unsigned block=0){
    auto d=std::make_unique<gal::Processor>(); d->reset(static_cast<float>(rate)); std::vector<float> out(in.size());
    const unsigned frames=static_cast<unsigned>(in.size()/2);
    if(!block) d->process(in.data(),out.data(),frames,2,2,p);
    else for(unsigned f=0;f<frames;f+=block){unsigned n=std::min(block,frames-f);d->process(in.data()+f*2,out.data()+f*2,n,2,2,p);}
    return out;
}
static double rms(const std::vector<float>& v,size_t from,int channel){double e=0;size_t n=0;for(size_t i=from*2+channel;i<v.size();i+=2){e+=double(v[i])*v[i];++n;}return std::sqrt(e/std::max<size_t>(1,n));}
static double db(double x){return 20*std::log10(std::max(x,1e-30));}
static std::vector<float> tone(double hz,double amp,int rate,int seconds=1){std::vector<float> v(static_cast<size_t>(rate)*seconds*2);for(size_t f=0;f<v.size()/2;++f)v[f*2]=v[f*2+1]=static_cast<float>(amp*std::sin(2*Pi*hz*f/rate));return v;}
// Character measurements bypass dynamics so a level change is the filter alone.
static gal::Parameters linearPath(){gal::Parameters p;p.wet=1;p.micHighpassHz=10;p.micLowpassHz=22000;p.quietGainDb=0;p.thresholdDbFs=0;p.ratio=1;p.ceiling=1;return p;}

int main(){
 gal::Parameters p; auto d=std::make_unique<gal::Processor>(); d->reset(48000); std::vector<float> in(4096),out(4096);
 for(size_t i=0;i<in.size();++i) in[i]=float(std::sin(i*.031)*.2);
 d->process(in.data(),out.data(),2048,2,2,p);check(in==out,"wet zero exact bypass");
 p.noiseDbFs=-40;p.presenceDb=6;p.lowShelfDb=-6;p.saturation=.5f;p.delayMs=3;d->process(in.data(),out.data(),2048,2,2,p);check(in==out,"wet zero bypasses every character stage");
 p=gal::Parameters{};
 p.wet=1; p.micHighpassHz=20;p.micLowpassHz=20000;p.ceiling=.3f;std::fill(in.begin(),in.end(),1);d->reset(48000);d->process(in.data(),out.data(),2048,2,2,p);for(float x:out)check(std::isfinite(x)&&std::abs(x)<=.30001f,"finite ceiling");
 d->reset(48000);std::fill(in.begin(),in.end(),.01f);for(int i=0;i<200;i+=2)in[i]=1;d->process(in.data(),out.data(),2048,2,2,p);check(d->gainReductionDb()>1,"linked detector compresses");
 in[0]=std::numeric_limits<float>::quiet_NaN();in[1]=std::numeric_limits<float>::infinity();d->process(in.data(),out.data(),2048,2,2,p);for(float x:out)check(std::isfinite(x),"nonfinite recovery");
 {auto a=std::make_unique<gal::Processor>(),b=std::make_unique<gal::Processor>();a->reset(48000);b->reset(48000);std::vector<float> oa(4096),ob(4096);std::fill(in.begin(),in.end(),.2f);a->process(in.data(),oa.data(),2048,2,2,p);for(int f=0;f<2048;f+=64)b->process(in.data()+f*2,ob.data()+f*2,64,2,2,p);float err=0;for(size_t i=0;i<oa.size();++i)err=std::max(err,std::abs(oa[i]-ob[i]));check(err<1e-6f,"block invariant");}
 p.micHighpassHz=500;p.micLowpassHz=12000;p.thresholdDbFs=-30;p.attackMs=0;p.holdMs=0;p.releaseMs=20;p.ceiling=1;
 {std::vector<float> low(96000),mid(96000),sink(96000);for(int f=0;f<48000;++f){float l=.2f*std::sin(6.2831853f*20*f/48000);float m=.2f*std::sin(6.2831853f*1000*f/48000);low[f*2]=low[f*2+1]=l;mid[f*2]=mid[f*2+1]=m;}
 auto lowD=std::make_unique<gal::Processor>(),midD=std::make_unique<gal::Processor>();lowD->reset(48000);midD->reset(48000);lowD->process(low.data(),sink.data(),48000,2,2,p);midD->process(mid.data(),sink.data(),48000,2,2,p);check(midD->gainReductionDb()>lowD->gainReductionDb()+6,"mic highpass precedes detector");}

 // ABI 2 defaults reproduce ABI 1 exactly: library defaults and the shipped prototype values.
 for(int rate:{44100,48000}) for(const char* name:{"impulse","sine","pink","silence"}) for(int config=0;config<2;++config){
  gal::Parameters q; q.wet=1; if(config){q.micHighpassHz=100;q.micLowpassHz=10000;q.ceiling=.5f;}
  std::vector<float> signal=stereo(name,rate),expected(signal.size());
  auto reference=std::make_unique<galv1::Processor>(); reference->reset(static_cast<float>(rate)); reference->process(signal.data(),expected.data(),static_cast<unsigned>(signal.size()/2),2,2,v1(q));
  check(render(q,signal,rate)==expected,"defaults bit-identical to ABI 1");
  check(render(q,signal,rate,256)==expected,"defaults bit-identical to ABI 1 in blocks");
  if(std::strcmp(name,"silence")){gal::Parameters moved=q; moved.presenceDb=.5f; check(render(moved,signal,rate)!=expected,"neutrality check can detect a change");}
 }

 // Self-noise: white, the requested RMS before gain, independent channels.
 {gal::Parameters q=linearPath(); q.noiseDbFs=-60; std::vector<float> silence(96000*2,0.0f); auto o=render(q,silence,48000);
  check(std::abs(db(rms(o,48000,0))+60)<=.5&&std::abs(db(rms(o,48000,1))+60)<=.5,"noise RMS -60 dBFS within 0.5 dB");
  double cross=0,l=0,r=0;for(size_t f=48000;f<96000;++f){cross+=double(o[f*2])*o[f*2+1];l+=double(o[f*2])*o[f*2];r+=double(o[f*2+1])*o[f*2+1];}
  check(std::abs(cross/std::sqrt(l*r))<.02,"noise channels independent");
  double mean=0;for(size_t f=0;f<96000;++f)mean+=o[f*2];check(std::abs(mean/96000)<1e-4,"noise has no DC");
  q.quietGainDb=6;auto boosted=render(q,silence,48000);check(std::abs(db(rms(boosted,48000,0))-db(rms(o,48000,0))-6)<=.1,"noise follows quiet gain");
  gal::Parameters off=linearPath(); off.noiseDbFs=-120; auto quiet=render(off,silence,48000);
  check(std::all_of(quiet.begin(),quiet.end(),[](float x){return x==0.0f;}),"noise at -120 dBFS is off");}

 // Noise enters before the detector: compression lowers it with a loud input.
 // The detector is stereo-linked, so a loud left channel compresses the right one, whose input is silent.
 {gal::Parameters q; q.wet=1; q.noiseDbFs=-50; q.ceiling=1; std::vector<float> silence(48000*2,0.0f),loud=tone(1000,.5,48000);
  for(size_t f=0;f<loud.size()/2;++f) loud[f*2+1]=0;
  auto compressed=render(q,loud,48000),idle=render(q,silence,48000);
  check(db(rms(compressed,24000,1))<db(rms(idle,24000,1))-10,"compression lowers the noise floor");}

 // Voicing: designed peak and shelf gains on steady sines.
 for(int rate:{44100,48000,96000}){
  gal::Parameters flat=linearPath(),voiced=linearPath(); voiced.presenceDb=6; voiced.presenceHz=3200;
  auto at=[&](const gal::Parameters& q,double hz){return db(rms(render(q,tone(hz,.1,rate),rate),rate/2,0));};
  check(std::abs(at(voiced,3200)-at(flat,3200)-6)<=.3,"presence +6 dB at its centre");
  check(std::abs(at(voiced,200)-at(flat,200))<=.3,"presence leaves 200 Hz");
  gal::Parameters shelf=linearPath(); shelf.lowShelfDb=-6; shelf.lowShelfHz=200;
  check(std::abs(at(shelf,200)-at(flat,200)+3)<=.3,"low shelf is half its gain at the corner");
  check(std::abs(at(shelf,40)-at(flat,40)+6)<=.5,"low shelf reaches its gain below the corner");
  check(std::abs(at(shelf,4000)-at(flat,4000))<=.1,"low shelf leaves the presence region");
 }

 // Saturation: exact clamp at zero, monotonic, never above the ceiling.
 {bool exact=true,monotonic=true,bounded=true;
  for(float x=-2.0f;x<=2.0f;x+=1.0f/4096){exact=exact&&gal::limit(x,.5f,0)==std::max(-.5f,std::min(.5f,x));}
  for(float s:{.1f,.4f,.7f,1.0f}){float previous=-1;for(float x=0;x<=4.0f;x+=1.0f/8192){float y=gal::limit(x,.5f,s);monotonic=monotonic&&y>=previous;bounded=bounded&&y<=.5f&&gal::limit(-x,.5f,s)==-y;previous=y;}
   check(gal::limit(.5f*(1-s)*.99f,.5f,s)==.5f*(1-s)*.99f,"saturation is linear below its knee");}
  check(exact,"saturation 0 is the ABI 1 clamp");check(monotonic,"saturation monotonic");check(bounded,"saturation bounded and odd");
  gal::Parameters q; q.wet=1; q.quietGainDb=24; q.ratio=1; q.saturation=.6f; q.ceiling=.4f; auto o=render(q,tone(1000,.9,48000),48000);
  check(std::all_of(o.begin(),o.end(),[](float x){return std::isfinite(x)&&std::abs(x)<=.4f;}),"saturated path stays under the ceiling");}

 // Delay: whole samples, exact shift of the electronic output.
 for(int rate:{44100,48000,96000,192000}) for(float ms:{2.0f,8.0f}){
  gal::Parameters q; q.wet=1; auto signal=stereo("impulse",rate); auto plain=render(q,signal,rate); q.delayMs=ms; auto late=render(q,signal,rate,333);
  const size_t shift=static_cast<size_t>(std::lround(ms*0.001*rate)); bool exact=true;
  for(size_t f=0;f<signal.size()/2;++f) for(int c=0;c<2;++c) exact=exact&&late[f*2+c]==(f<shift?0.0f:plain[(f-shift)*2+c]);
  check(exact,"delay shifts by round(ms * rate) samples");}

 // Band order 2: Butterworth edges at the same corners, -3 dB there and about -12 dB an octave out.
 for(int rate:{44100,48000,96000}){
  auto at=[&](const gal::Parameters& q,double hz){return db(rms(render(q,tone(hz,.1,rate),rate),rate/2,0));};
  gal::Parameters lp1=linearPath(); lp1.micLowpassHz=2000; gal::Parameters lp2=lp1; lp2.bandOrder=2;
  check(std::abs(at(lp2,2000)-at(lp2,250)+3)<=.3,"steep low-pass is -3 dB at its corner");
  check(std::abs(at(lp2,4000)-at(lp2,250)+12.3)<=.8,"steep low-pass falls 12 dB per octave");
  check(at(lp2,4000)<at(lp1,4000)-4,"steep low-pass is steeper than the first-order edge");
  gal::Parameters hp2=linearPath(); hp2.micHighpassHz=400; hp2.bandOrder=2;
  check(std::abs(at(hp2,400)-at(hp2,3200)+3)<=.3,"steep high-pass is -3 dB at its corner");
  check(std::abs(at(hp2,200)-at(hp2,3200)+12.3)<=.8,"steep high-pass falls 12 dB per octave");
  gal::Parameters one=linearPath(); one.micLowpassHz=2000; one.bandOrder=1.4f;
  check(render(one,tone(1000,.1,rate),rate)==render(lp1,tone(1000,.1,rate),rate),"band order below 1.5 is the first-order path");
 }

 // Every stage on: finite, block invariant, deterministic after reset.
 {gal::Parameters q; q.wet=1; q.noiseDbFs=-45; q.presenceDb=4; q.lowShelfDb=-4; q.saturation=.4f; q.delayMs=3; q.ceiling=.5f; q.bandOrder=2; q.micHighpassHz=250; q.micLowpassHz=8000;
  auto signal=stereo("pink",48000); signal[100]=std::numeric_limits<float>::quiet_NaN(); signal[101]=-std::numeric_limits<float>::infinity();
  auto whole=render(q,signal,48000),blocks=render(q,signal,48000,97);
  check(std::all_of(whole.begin(),whole.end(),[](float x){return std::isfinite(x);}),"character stages finite after nonfinite input");
  check(whole==blocks,"character stages block invariant");
  auto e=std::make_unique<gal::Processor>(); e->reset(48000); std::vector<float> first(signal.size()),second(signal.size());
  e->process(signal.data(),first.data(),48000,2,2,q); e->reset(48000); e->process(signal.data(),second.data(),48000,2,2,q);
  check(first==second,"reset restores filters, delay line and noise generator");}

 std::cout<<(fails?"FAILED ":"PASS ")<<"native DSP invariants"<<'\n';return fails?1:0;
}
