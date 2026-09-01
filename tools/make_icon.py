#!/usr/bin/env python3
"""
Gera PCOptimizer/Assets/icon.ico — simbolo radioativo (trefoil) preto
sobre o verde #22C55E do proprio app.

Python puro (zlib + struct da biblioteca padrao). Nao precisa de Pillow,
ImageMagick nem Inkscape, e nao adiciona dependencia ao projeto.

Cada tamanho e desenhado do zero, nao reduzido do 256: em 16px o trefoil
precisa de margem menor e traco proporcionalmente mais grosso, senao vira
borrao na bandeja do sistema.

Uso:  python3 tools/make_icon.py
"""

import math
import os
import struct
import sys
import zlib

# ---------------------------------------------------------------- parametros

GREEN = (0x22, 0xC5, 0x5E)      # verde do app (#22C55E), usado nos cards
BLACK = (0x00, 0x00, 0x00)      # simbolo, como na placa de radiacao real

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]   # 100/125/150/175/200% do Windows
SS = 4                                            # supersampling p/ antialiasing

# Geometria oficial ISO do trefoil, em fracoes do raio do simbolo (R):
DISC = 0.20        # disco central
BLADE_IN = 0.32    # onde a lamina comeca
BLADE_OUT = 1.00   # onde a lamina termina
BLADE_HALF = 30.0  # meia-abertura da lamina, em graus (laminas de 60, vaos de 60)
BLADE_ANGLES = (90.0, 210.0, 330.0)   # uma lamina para cima, vao para baixo


def symbol_radius(n):
    """Raio do trefoil. Em icones pequenos a margem encolhe para o desenho
    nao virar uma mancha de poucos pixels."""
    if n <= 16:
        return 0.46 * n
    if n <= 24:
        return 0.44 * n
    if n <= 48:
        return 0.42 * n
    return 0.40 * n


# A lamina de cima chega a 1.00R, mas as de baixo so a sen(60) = 0.87R.
# Centrado na geometria, o simbolo parece deslocado para cima; descemos
# metade da diferenca para ele ficar opticamente no meio.
Y_NUDGE = (BLADE_OUT - BLADE_OUT * math.sin(math.radians(60))) / 2.0


def corner_radius(n):
    """Canto arredondado, no estilo dos icones do Windows."""
    return 0.16 * n


# ------------------------------------------------------------------ desenho

def in_trefoil(dx, dy, r):
    d = math.hypot(dx, dy)
    if d <= DISC * r:
        return True
    if d < BLADE_IN * r or d > BLADE_OUT * r:
        return False
    a = math.degrees(math.atan2(-dy, dx)) % 360.0   # -dy: y cresce para baixo
    for c in BLADE_ANGLES:
        delta = abs((a - c + 180.0) % 360.0 - 180.0)
        if delta <= BLADE_HALF:
            return True
    return False


def in_rounded_square(x, y, n, cr):
    if x < 0 or y < 0 or x > n or y > n:
        return False
    cx = min(max(x, cr), n - cr)
    cy = min(max(y, cr), n - cr)
    return math.hypot(x - cx, y - cy) <= cr


def render(n):
    """Devolve RGBA (bytes) de n x n, com antialiasing por supersampling."""
    r = symbol_radius(n)
    cr = corner_radius(n)
    c = n / 2.0
    step = 1.0 / SS
    half = step / 2.0
    samples = SS * SS

    out = bytearray(n * n * 4)
    for py in range(n):
        for px in range(n):
            cover = 0      # dentro do quadrado arredondado
            ink = 0        # dentro do trefoil
            for sy in range(SS):
                y = py + sy * step + half
                for sx in range(SS):
                    x = px + sx * step + half
                    if not in_rounded_square(x, y, n, cr):
                        continue
                    cover += 1
                    if in_trefoil(x - c, y - c - r * Y_NUDGE, r):
                        ink += 1

            i = (py * n + px) * 4
            if cover == 0:
                continue                       # transparente
            t = ink / cover                    # fracao de preto sobre o verde
            out[i + 0] = round(BLACK[0] * t + GREEN[0] * (1 - t))
            out[i + 1] = round(BLACK[1] * t + GREEN[1] * (1 - t))
            out[i + 2] = round(BLACK[2] * t + GREEN[2] * (1 - t))
            out[i + 3] = round(255 * cover / samples)
    return bytes(out)


# ------------------------------------------------------------ PNG e ICO cru

def png(w, h, rgba):
    def chunk(tag, data):
        body = tag + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xFFFFFFFF)

    raw = b''.join(b'\x00' + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def ico(entries):
    """entries: lista de (tamanho, bytes_png). ICO com PNG embutido em cada
    entrada — formato aceito pelo Windows Vista em diante."""
    n = len(entries)
    header = struct.pack('<HHH', 0, 1, n)
    offset = 6 + 16 * n
    dir_bytes = b''
    data_bytes = b''
    for size, blob in entries:
        dim = 0 if size >= 256 else size       # 0 significa 256 no formato ICO
        dir_bytes += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        data_bytes += blob
    return header + dir_bytes + data_bytes


# ---------------------------------------------------------------------- main

def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ico_path = os.path.join(root, 'PCOptimizer', 'Assets', 'icon.ico')
    preview_path = os.path.join(root, 'tools', 'icon-preview-256.png')

    entries = []
    for n in SIZES:
        blob = png(n, n, render(n))
        entries.append((n, blob))
        print(f'  {n:>3}x{n:<3}  {len(blob):>6} bytes')
        if n == 256:
            with open(preview_path, 'wb') as f:
                f.write(blob)

    data = ico(entries)
    os.makedirs(os.path.dirname(ico_path), exist_ok=True)
    with open(ico_path, 'wb') as f:
        f.write(data)

    print(f'\n{ico_path}  ({len(data)} bytes, {len(entries)} resolucoes)')
    print(f'{preview_path}  (previa)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
