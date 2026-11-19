// Other players: a faceless office-worker figure built from boxes and lit with the same shader
// as the props, using the textures of whichever chunk it is standing in (so it gets the panel
// lighting, soft wall shadows and dark zones like everything else). Plus a name tag and footsteps.
import * as THREE from 'three';
import type { ChunkManager } from './chunk';
import type { Peer } from './net';
import { GeoBuilder, emitProp, type Box, type Template } from './props';

function figure(color: string): THREE.BufferGeometry {
  const c = new THREE.Color(color);
  // shirt colour pulled towards the beige of the place, so nobody looks like a game character
  const shirt: [number, number, number] = [c.r * 0.55 + 0.25, c.g * 0.55 + 0.23, c.b * 0.55 + 0.17];
  const palette: [number, number, number][] = [shirt, [0.16, 0.15, 0.14], [0.66, 0.55, 0.45], [0.1, 0.09, 0.08]];
  const boxes: Box[] = [
    [-0.1, 0.43, 0, 0.15, 0.86, 0.17, 1], // legs
    [0.1, 0.43, 0, 0.15, 0.86, 0.17, 1],
    [-0.1, 0.03, 0.04, 0.15, 0.06, 0.26, 3], // shoes
    [0.1, 0.03, 0.04, 0.15, 0.06, 0.26, 3],
    [0, 1.17, 0, 0.42, 0.62, 0.24, 0], // torso
    [-0.27, 1.13, 0, 0.1, 0.62, 0.12, 0], // arms
    [0.27, 1.13, 0, 0.1, 0.62, 0.12, 0],
    [-0.27, 0.78, 0, 0.08, 0.1, 0.09, 2], // hands
    [0.27, 0.78, 0, 0.08, 0.1, 0.09, 2],
    [0, 1.52, 0, 0.1, 0.1, 0.1, 2], // neck
    [0, 1.67, 0, 0.21, 0.25, 0.23, 2], // head (no face)
    [0, 1.81, -0.01, 0.22, 0.05, 0.24, 3], // hair
  ];
  // the template faces +z; players face -z at yaw 0, so turn it around
  const t: Template = { boxes, h: 1.85, d: 0.3 };
  const g = new GeoBuilder();
  emitProp(g, t, new THREE.Matrix4().makeRotationY(Math.PI), 0, 0.5, false, palette);
  return g.geometry()!;
}

/** The entity: too tall, too thin, arms hanging past its knees, no face. */
function entityFigure(): THREE.BufferGeometry {
  const palette: [number, number, number][] = [
    [0.035, 0.032, 0.03],
    [0.06, 0.055, 0.05],
    [0.02, 0.02, 0.02],
  ];
  const boxes: Box[] = [
    [-0.08, 0.62, 0, 0.09, 1.24, 0.1, 0], // legs
    [0.08, 0.62, 0, 0.09, 1.24, 0.1, 0],
    [0, 1.62, 0.02, 0.3, 0.78, 0.15, 1], // narrow torso, hunched forward a little
    [-0.2, 1.22, 0.04, 0.06, 1.25, 0.07, 0], // arms to below the knees
    [0.2, 1.22, 0.04, 0.06, 1.25, 0.07, 0],
    [-0.2, 0.55, 0.05, 0.05, 0.16, 0.04, 2], // long fingers
    [0.2, 0.55, 0.05, 0.05, 0.16, 0.04, 2],
    [0, 2.08, 0.06, 0.06, 0.18, 0.06, 0], // neck
    [0, 2.28, 0.1, 0.16, 0.3, 0.17, 1], // small, long head
  ];
  const g = new GeoBuilder();
  emitProp(g, { boxes, h: 2.45, d: 0.2 }, new THREE.Matrix4().makeRotationY(Math.PI), 0, 0.31, false, palette);
  return g.geometry()!;
}

function nameTag(name: string, color: string): THREE.Sprite {
  const cv = document.createElement('canvas');
  cv.width = 256;
  cv.height = 64;
  const c = cv.getContext('2d')!;
  c.font = '28px "Courier New", monospace';
  c.textAlign = 'center';
  c.textBaseline = 'middle';
  c.fillStyle = 'rgba(0,0,0,0.45)';
  const w = Math.min(250, c.measureText(name).width + 34);
  c.fillRect(128 - w / 2, 14, w, 36);
  c.fillStyle = color;
  c.fillRect(128 - w / 2, 14, 5, 36);
  c.fillStyle = '#f2f0e6';
  c.fillText(name, 130, 33);
  const tex = new THREE.CanvasTexture(cv);
  const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, transparent: true, depthWrite: false }));
  s.scale.set(1.0, 0.25, 1);
  s.position.y = 2.15;
  return s;
}

interface Avatar {
  group: THREE.Group;
  mesh: THREE.Mesh;
  mat: THREE.ShaderMaterial;
  tag: THREE.Sprite;
  stepDist: number;
  lastX: number;
  lastZ: number;
  phase: number;
  color: string;
  entity: boolean;
}

export class Avatars {
  private list = new Map<number, Avatar>();

  constructor(
    private scene: THREE.Scene,
    private propMat: THREE.ShaderMaterial,
    private shared: Record<string, THREE.IUniform>,
    private chunks: ChunkManager,
    private onStep: (x: number, z: number, heavy: boolean) => void,
  ) {}

  private litMaterial() {
    const mat = this.propMat.clone();
    Object.assign(mat.uniforms, this.shared);
    mat.uniforms.uOcc = { value: null };
    mat.uniforms.uLights = { value: null };
    mat.uniforms.uChunkOrigin = { value: new THREE.Vector2() };
    mat.uniforms.uLightBase = { value: new THREE.Vector2() };
    return mat;
  }

  add(p: Peer) {
    const mat = this.litMaterial();
    const mesh = new THREE.Mesh(figure(p.color), mat);
    mesh.frustumCulled = false;
    const group = new THREE.Group();
    group.add(mesh);
    const tag = nameTag(p.name, p.color);
    group.add(tag);
    group.visible = false;
    this.scene.add(group);
    this.list.set(p.id, { group, mesh, mat, tag, stepDist: 0, lastX: NaN, lastZ: NaN, phase: 0, color: p.color, entity: false });
  }

  remove(id: number) {
    const a = this.list.get(id);
    if (!a) return;
    this.scene.remove(a.group);
    a.mesh.geometry.dispose();
    a.mat.dispose();
    (a.tag.material as THREE.SpriteMaterial).map?.dispose();
    a.tag.material.dispose();
    this.list.delete(id);
  }

  /** Swap between the office worker and the entity. */
  private setEntity(a: Avatar, entity: boolean) {
    if (a.entity === entity) return;
    a.entity = entity;
    a.mesh.geometry.dispose();
    a.mesh.geometry = entity ? entityFigure() : figure(a.color);
  }

  /** Light a mesh as if it stood in the chunk at (x, z). Returns false when that chunk isn't loaded. */
  light(mat: THREE.ShaderMaterial, x: number, z: number): boolean {
    const chunk = this.chunks.at(x, z);
    if (!chunk) return false;
    const u = mat.uniforms;
    u.uOcc.value = chunk.env.occ;
    u.uLights.value = chunk.env.lights;
    u.uChunkOrigin.value = chunk.env.origin;
    u.uLightBase.value = chunk.env.lbase;
    return true;
  }

  update(peers: Iterable<Peer>, camX: number, camZ: number, isEntity: (id: number) => boolean = () => false) {
    for (const p of peers) {
      const a = this.list.get(p.id);
      if (!a) continue;
      this.setEntity(a, isEntity(p.id));
      const chunk = this.chunks.at(p.x, p.z);
      a.group.visible = p.visible && !!chunk;
      // a phasing entity only half exists
      if (a.entity && p.f & 2 && Math.random() < 0.55) a.group.visible = false;
      a.tag.visible = !a.entity;
      if (!a.group.visible || !chunk) continue;
      // light it like the chunk it stands in
      const u = a.mat.uniforms;
      u.uOcc.value = chunk.env.occ;
      u.uLights.value = chunk.env.lights;
      u.uChunkOrigin.value = chunk.env.origin;
      u.uLightBase.value = chunk.env.lbase;
      // walk cycle: a bob and a sway, driven by how far they actually moved
      const moved = Number.isNaN(a.lastX) ? 0 : Math.hypot(p.x - a.lastX, p.z - a.lastZ);
      a.lastX = p.x;
      a.lastZ = p.z;
      a.phase += moved / (0.62 + 0.11 * p.v);
      const walking = Math.min(1, p.v / 1.7);
      const running = Math.max(0, Math.min(1, (p.v - 1.7) / 3.9));
      if (a.entity) {
        // no bob: it glides, and now and then it is somewhere slightly else for a frame
        const j = Math.random() < 0.04 ? 0.12 : 0;
        a.group.position.set(p.x + (Math.random() - 0.5) * j, 0, p.z + (Math.random() - 0.5) * j);
        a.group.rotation.set(0.1, p.yaw + (Math.random() - 0.5) * j, Math.sin(performance.now() * 0.0007 + p.id) * 0.05, 'YXZ');
      } else {
        a.group.position.set(p.x, Math.abs(Math.sin(a.phase * Math.PI)) * (0.035 + 0.05 * running) * walking, p.z);
        // runners lean forward
        a.group.rotation.set(-0.16 * running, p.yaw, Math.sin(a.phase * Math.PI) * 0.025 * walking, 'YXZ');
      }
      a.stepDist += moved;
      if (a.stepDist > (a.entity ? 1.3 : 0.62 + 0.11 * p.v)) {
        a.stepDist = 0;
        if ((p.x - camX) ** 2 + (p.z - camZ) ** 2 < 40 * 40) this.onStep(p.x, p.z, a.entity);
      }
      // name tags fade out with distance
      const d = Math.hypot(p.x - camX, p.z - camZ);
      a.tag.material.opacity = Math.max(0, Math.min(1, (22 - d) / 8));
    }
  }
}

/** The way out: a lone door frame standing in the open, white light pouring through it. */
export class ExitDoor {
  group = new THREE.Group();
  private mat: THREE.ShaderMaterial;

  constructor(
    private scene: THREE.Scene,
    private avatars: Avatars,
    propMat: THREE.ShaderMaterial,
    shared: Record<string, THREE.IUniform>,
  ) {
    this.mat = propMat.clone();
    Object.assign(this.mat.uniforms, shared);
    this.mat.uniforms.uOcc = { value: null };
    this.mat.uniforms.uLights = { value: null };
    this.mat.uniforms.uChunkOrigin = { value: new THREE.Vector2() };
    this.mat.uniforms.uLightBase = { value: new THREE.Vector2() };
    const g = new GeoBuilder();
    const frame: Box[] = [
      [-0.5, 1.06, 0, 0.08, 2.12, 0.16, 0],
      [0.5, 1.06, 0, 0.08, 2.12, 0.16, 0],
      [0, 2.15, 0, 1.08, 0.08, 0.16, 0],
      [0, 0.01, 0, 1.08, 0.02, 0.16, 0],
    ];
    const light: Box[] = [[0, 1.06, 0, 0.92, 2.1, 0.02, 0]];
    const id = new THREE.Matrix4();
    emitProp(g, { boxes: frame, h: 2.2, d: 0.16 }, id, 0, 0.7, false, [[0.8, 0.79, 0.74]]);
    emitProp(g, { boxes: light, h: 2.2, d: 0.02 }, id, 4, 0.7, false, [[1.0, 0.97, 0.88]]);
    const mesh = new THREE.Mesh(g.geometry()!, this.mat);
    mesh.frustumCulled = false;
    this.group.add(mesh);
    this.group.visible = false;
    scene.add(this.group);
  }

  update(exit: { x: number; z: number } | null) {
    if (!exit) {
      this.group.visible = false;
      return;
    }
    this.group.position.set(exit.x, 0, exit.z);
    this.group.visible = this.avatars.light(this.mat, exit.x, exit.z);
  }

  dispose() {
    this.scene.remove(this.group);
  }
}
