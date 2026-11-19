// Frame pipeline: HDR scene (MSAA) -> two-level bloom -> camcorder grade (tone map, yellow cast,
// lifted blacks, chroma bleed, chromatic aberration, grain, vignette, tape wobble).
import * as THREE from 'three';

const fsVert = /* glsl */ `
varying vec2 vUv;
void main() { vUv = position.xy * 0.5 + 0.5; gl_Position = vec4(position.xy, 0.0, 1.0); }
`;

const brightFrag = /* glsl */ `
uniform sampler2D tSrc; uniform vec2 uTexel; varying vec2 vUv;
void main() {
  vec3 c = vec3(0.0);
  c += texture2D(tSrc, vUv + uTexel * vec2(-1.0, -1.0)).rgb;
  c += texture2D(tSrc, vUv + uTexel * vec2( 1.0, -1.0)).rgb;
  c += texture2D(tSrc, vUv + uTexel * vec2(-1.0,  1.0)).rgb;
  c += texture2D(tSrc, vUv + uTexel * vec2( 1.0,  1.0)).rgb;
  c *= 0.25;
  float l = dot(c, vec3(0.299, 0.587, 0.114));
  gl_FragColor = vec4(c * smoothstep(1.2, 3.5, l), 1.0);
}
`;

const blurFrag = /* glsl */ `
uniform sampler2D tSrc; uniform vec2 uDir; varying vec2 vUv;
void main() {
  vec3 c = texture2D(tSrc, vUv).rgb * 0.227027;
  c += texture2D(tSrc, vUv + uDir * 1.3846).rgb * 0.316216;
  c += texture2D(tSrc, vUv - uDir * 1.3846).rgb * 0.316216;
  c += texture2D(tSrc, vUv + uDir * 3.2308).rgb * 0.070270;
  c += texture2D(tSrc, vUv - uDir * 3.2308).rgb * 0.070270;
  gl_FragColor = vec4(c, 1.0);
}
`;

const finalFrag = /* glsl */ `
uniform sampler2D tScene; uniform sampler2D tB1; uniform sampler2D tB2;
uniform vec2 uRes; uniform float uTime; uniform float uVhs; uniform float uExposure; uniform float uFade;
uniform float uDread;  // 0..1: an entity is near, the tape starts to fail
uniform float uHunter; // 0..1: we are an entity: see the place drained and red
uniform float uGlitch; // 0..1: just got caught
varying vec2 vUv;
float h21(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
vec3 aces(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }
vec3 sceneCA(vec2 uv, float amt) {
  vec2 d = uv - 0.5;
  vec2 o = d * dot(d, d) * amt;
  return vec3(texture2D(tScene, uv + o).r, texture2D(tScene, uv).g, texture2D(tScene, uv - o).b);
}
void main() {
  vec2 uv = vUv;
  vec2 d = uv - 0.5;
  uv = 0.5 + d * (1.0 - 0.02 * uVhs + 0.04 * uVhs * dot(d, d)); // slight camcorder barrel
  // tape: per-line horizontal jitter and a slow tracking wobble
  float line = floor(uv.y * uRes.y / 2.0);
  float tf = floor(uTime * 30.0);
  float fail = max(uDread * uDread, uGlitch);
  uv.x += (h21(vec2(line, tf)) - 0.5) * (0.0009 * uVhs + 0.006 * fail);
  // tearing bands that roll through the frame as it closes in
  float tear = step(1.0 - 0.2 * fail, h21(vec2(floor(uv.y * 24.0), tf))) * fail;
  uv.x += tear * (h21(vec2(tf, floor(uv.y * 24.0))) - 0.5) * 0.05;
  uv.y += uGlitch * (h21(vec2(tf, 1.0)) - 0.5) * 0.05;
  uv.x += sin(uv.y * 9.0 + uTime * 1.3) * 0.0006 * uVhs;
  float bandOn = step(0.8, h21(vec2(floor(uTime / 6.0), 7.0)));
  float band = smoothstep(0.025, 0.0, abs(uv.y - fract(uTime / 6.0) * 1.3 + 0.15)) * uVhs * bandOn;
  uv.x += band * (h21(vec2(line, tf + 3.0)) - 0.5) * 0.01;

  vec3 c = sceneCA(uv, 0.03 * uVhs + 0.004 + 0.12 * fail);
  // chroma bleed: luma sharp, chroma smeared to the right like composite video
  vec2 px = vec2(1.0 / uRes.x, 0.0);
  vec3 sm = (texture2D(tScene, uv - px * 2.0).rgb + texture2D(tScene, uv - px * 5.0).rgb + texture2D(tScene, uv - px * 9.0).rgb + c) * 0.25;
  float Y = dot(c, vec3(0.299, 0.587, 0.114));
  float Ys = dot(sm, vec3(0.299, 0.587, 0.114));
  c = mix(c, sm - Ys + Y, 0.65 * uVhs);

  vec3 bloom = texture2D(tB1, uv).rgb * 0.7 + texture2D(tB2, uv).rgb * 1.1;
  c += bloom * 0.35;
  c *= uExposure;
  c = aces(c);
  // grade: warm/yellow cast, crushed blue, lifted blacks, softened whites
  c = pow(c, vec3(0.95, 0.97, 1.2));
  float l = dot(c, vec3(0.299, 0.587, 0.114));
  c = mix(vec3(l), c, 0.98);
  c = c * vec3(1.02, 1.0, 0.88) * 0.95 + vec3(0.025, 0.022, 0.01);
  // vignette
  // the entity's view: drained, rusty, darker at the edges
  float lh = dot(c, vec3(0.299, 0.587, 0.114));
  c = mix(c, vec3(lh * 1.15, lh * 0.82, lh * 0.7), 0.75 * uHunter);
  c = mix(c, vec3(dot(c, vec3(0.333))), 0.35 * fail);
  float v = smoothstep(0.95 - 0.25 * max(uHunter, uDread), 0.25, length(d * vec2(1.0, 0.85)));
  c *= mix(0.62, 1.0, v);
  // grain (stronger in the darks) and faint scanlines
  float g = h21(gl_FragCoord.xy + fract(uTime * 7.31) * 100.0) - 0.5;
  c += g * (mix(0.045, 0.025, l) + 0.14 * fail);
  c += tear * 0.12;
  c *= 1.0 - 0.035 * uVhs * step(0.5, fract(gl_FragCoord.y * 0.5));
  c += band * 0.03;
  c = pow(max(c, 0.0), vec3(1.0 / 2.2));
  c *= uFade;
  gl_FragColor = vec4(c, 1.0);
}
`;

export class Post {
  private sceneRT: THREE.WebGLRenderTarget;
  private a1: THREE.WebGLRenderTarget;
  private b1: THREE.WebGLRenderTarget;
  private a2: THREE.WebGLRenderTarget;
  private b2: THREE.WebGLRenderTarget;
  private quad: THREE.Mesh;
  private cam = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
  private fsScene = new THREE.Scene();
  private bright: THREE.ShaderMaterial;
  private blur: THREE.ShaderMaterial;
  final: THREE.ShaderMaterial;

  constructor(
    private renderer: THREE.WebGLRenderer,
    w: number,
    h: number,
    samples: number,
  ) {
    const opt = { type: THREE.HalfFloatType, depthBuffer: false };
    this.sceneRT = new THREE.WebGLRenderTarget(w, h, { type: THREE.HalfFloatType, samples, depthBuffer: true });
    this.a1 = new THREE.WebGLRenderTarget(1, 1, opt);
    this.b1 = new THREE.WebGLRenderTarget(1, 1, opt);
    this.a2 = new THREE.WebGLRenderTarget(1, 1, opt);
    this.b2 = new THREE.WebGLRenderTarget(1, 1, opt);
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 3, -1, 0, -1, 3, 0], 3));
    const mk = (frag: string, uniforms: Record<string, THREE.IUniform>) =>
      new THREE.ShaderMaterial({ vertexShader: fsVert, fragmentShader: frag, uniforms, depthTest: false, depthWrite: false });
    this.bright = mk(brightFrag, { tSrc: { value: null }, uTexel: { value: new THREE.Vector2() } });
    this.blur = mk(blurFrag, { tSrc: { value: null }, uDir: { value: new THREE.Vector2() } });
    this.final = mk(finalFrag, {
      tScene: { value: null },
      tB1: { value: null },
      tB2: { value: null },
      uRes: { value: new THREE.Vector2() },
      uTime: { value: 0 },
      uVhs: { value: 1 },
      uExposure: { value: 1.0 },
      uFade: { value: 1 },
      uDread: { value: 0 },
      uHunter: { value: 0 },
      uGlitch: { value: 0 },
    });
    this.quad = new THREE.Mesh(geo, this.bright);
    this.quad.frustumCulled = false;
    this.fsScene.add(this.quad);
    this.setSize(w, h);
  }

  setSize(w: number, h: number) {
    this.sceneRT.setSize(w, h);
    const w1 = Math.max(1, w >> 2), h1 = Math.max(1, h >> 2);
    const w2 = Math.max(1, w >> 3), h2 = Math.max(1, h >> 3);
    this.a1.setSize(w1, h1);
    this.b1.setSize(w1, h1);
    this.a2.setSize(w2, h2);
    this.b2.setSize(w2, h2);
    this.final.uniforms.uRes.value.set(w, h);
  }

  private pass(mat: THREE.ShaderMaterial, target: THREE.WebGLRenderTarget | null) {
    this.quad.material = mat;
    this.renderer.setRenderTarget(target);
    this.renderer.render(this.fsScene, this.cam);
  }

  render(scene: THREE.Scene, camera: THREE.Camera, time: number) {
    const r = this.renderer;
    r.setRenderTarget(this.sceneRT);
    r.render(scene, camera);
    // bloom level 1 (1/4)
    this.bright.uniforms.tSrc.value = this.sceneRT.texture;
    this.bright.uniforms.uTexel.value.set(1 / this.sceneRT.width, 1 / this.sceneRT.height);
    this.pass(this.bright, this.a1);
    this.blurPass(this.a1, this.b1);
    // level 2 (1/8)
    this.blur.uniforms.tSrc.value = this.a1.texture;
    this.blur.uniforms.uDir.value.set(0, 0);
    this.pass(this.blur, this.a2);
    this.blurPass(this.a2, this.b2);
    this.blurPass(this.a2, this.b2);
    const f = this.final.uniforms;
    f.tScene.value = this.sceneRT.texture;
    f.tB1.value = this.a1.texture;
    f.tB2.value = this.a2.texture;
    f.uTime.value = time;
    this.pass(this.final, null);
  }

  private blurPass(a: THREE.WebGLRenderTarget, b: THREE.WebGLRenderTarget) {
    this.blur.uniforms.tSrc.value = a.texture;
    this.blur.uniforms.uDir.value.set(1 / a.width, 0);
    this.pass(this.blur, b);
    this.blur.uniforms.tSrc.value = b.texture;
    this.blur.uniforms.uDir.value.set(0, 1 / a.height);
    this.pass(this.blur, a);
  }
}
