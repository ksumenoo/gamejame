"""
Пайплайн звуков «Выдержки»: сырые записи → чистые дубли под ключи SoundBank.
python process.py <папка_сырых> <папка_выхода>
Имя ключа берётся из имени файла: Candle_out_v2.m4a → candle_out. Несколько файлов одного ключа объединяются.
"""
import glob, os, re, subprocess, sys, json
import numpy as np, soundfile as sf

RAW, OUT = sys.argv[1], sys.argv[2]
SR = 44100

# ключ → (макс. длина дубля, с; склейка пауз короче, с; сколько дублей оставить; целевой пик, dBFS)
PROFILE = {
    'default':          (3.0, 0.15, 5, -3.0),
    'candle_out':       (2.0, 0.20, 4, -4.0),
    'candle_light':     (2.5, 0.20, 4, -4.0),
    'flash_empty':      (1.0, 0.05, 5, -6.0),
    'scene_align':      (3.0, 0.08, 4, -8.0),
    'margaret_whisper': (4.0, 0.45, 6, -6.0),
    'footstep':         (1.0, 0.05, 8, -6.0),
    'exposure_tick':    (0.6, 0.03, 4, -8.0),
}
# шумодав (дБ), порог над фоном (дБ), минимальная длина дубля (с), окно огибающей (с)
EXTRA = {'default': dict(nr=14, thr=10, mindur=0.05, win=0.01),
         'margaret_whisper': dict(nr=6, thr=6, mindur=0.5, win=0.04)}
LOOPS = {'ambient'}  # не режем, делаем петлю


def key_of(path):
    n = os.path.splitext(os.path.basename(path))[0].lower()
    n = re.sub(r'[_\- ]?v\d+$', '', n)
    n = re.sub(r'[_\- ]?\d+$', '', n)
    return n


def decode_clean(path, tmp, nr=14):
    """ffmpeg: моно 44.1 кГц, срез гула, шумодав с отслеживанием фона."""
    subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', path, '-ac', '1', '-ar', str(SR),
                    '-af', f'highpass=f=70,afftdn=nr={nr}:nf=-60:tn=1', tmp], check=True)
    raw = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, '-ac', '1', '-ar', str(SR), '-f', 'f32le', '-'],
                         capture_output=True, check=True).stdout
    return sf.read(tmp)[0], np.frombuffer(raw, np.float32)


def env_db(x, win):
    n = len(x) // win
    e = np.sqrt((x[:n * win].reshape(n, win) ** 2).mean(1) + 1e-12)
    return 20 * np.log10(e)


def takes(x, raw, prof, ex):
    """Дубли с запасом: от тишины до тишины. Начало — за 80 мс до первого подъёма над фоном,
    конец — когда сигнал вернулся к фону, плюс 150 мс. Соседний дубль не задевается."""
    maxlen, gap, _, _ = prof
    step = ex['win']
    win = int(step * SR)
    db = env_db(x, win)
    db = np.convolve(db, np.ones(3) / 3, mode='same')  # сглаживаем, чтобы край не прыгал
    floor = np.percentile(db, 25)
    on = db > floor + ex['thr']
    quiet = floor + 2.5
    g = max(1, int(gap / step))
    n = len(db)
    cores, i = [], 0
    while i < n:
        if not on[i]: i += 1; continue
        j = i
        while j < n and (on[j] or on[j:j + g].any()): j += 1
        cores.append([i, j]); i = j + 1
    out = []
    for c, (i, j) in enumerate(cores):
        lo = cores[c - 1][1] if c > 0 else 0
        hi = cores[c + 1][0] if c + 1 < len(cores) else n
        s0 = i
        while s0 > lo and db[s0 - 1] > quiet: s0 -= 1
        e0 = j
        while e0 < hi and db[e0] > quiet: e0 += 1
        a = max(lo * win, s0 * win - int(0.08 * SR))
        b = min(hi * win - int(0.02 * SR), e0 * win + int(0.15 * SR), len(x))
        if b <= a: continue
        seg = x[a:b]
        dur = len(seg) / SR
        peak_raw = np.abs(raw[a:min(b, len(raw))]).max()
        snr = db[i:max(j, i + 1)].max() - floor
        # края: громкость первых 20 и последних 40 мс относительно фона
        head = 20 * np.log10(np.sqrt((seg[:int(0.02 * SR)] ** 2).mean() + 1e-12)) - floor
        tail = 20 * np.log10(np.sqrt((seg[-int(0.04 * SR):] ** 2).mean() + 1e-12)) - floor
        out.append(dict(seg=seg, start=a / SR, dur=dur, snr=snr, clipped=peak_raw > 0.98,
                        too_long=dur > maxlen, head=head, tail=tail))
    return out, floor


def finish(seg, peak_db):
    seg = seg - seg.mean()
    fi = int(0.01 * SR)
    fo = int(min(0.2, len(seg) / SR * 0.25) * SR)
    seg[:fi] *= np.sin(np.linspace(0, np.pi / 2, fi)) ** 2
    if fo > 0: seg[-fo:] *= np.cos(np.linspace(0, np.pi / 2, fo)) ** 2
    p = np.abs(seg).max()
    return seg * (10 ** (peak_db / 20) / p) if p > 0 else seg


def make_loop(x, peak_db=-12.0, xf=2.0):
    """Петля: конец плавно переходит в начало (кроссфейд xf секунд)."""
    n = int(xf * SR)
    head, body = x[:n], x[n:]
    t = np.linspace(0, 1, n)
    body[-n:] = body[-n:] * np.cos(t * np.pi / 2) + head * np.sin(t * np.pi / 2)
    p = np.abs(body).max()
    return body * (10 ** (peak_db / 20) / p)


report = {}
os.makedirs(OUT, exist_ok=True)
files = sorted(glob.glob(os.path.join(RAW, '*.*')))
by_key = {}
for f in files:
    if f.lower().endswith(('.m4a', '.wav', '.mp3', '.ogg', '.aac', '.flac', '.opus')):
        by_key.setdefault(key_of(f), []).append(f)

for key, fs in by_key.items():
    prof = PROFILE.get(key, PROFILE['default'])
    ex = EXTRA.get(key, EXTRA['default'])
    if key in LOOPS:
        x, _ = decode_clean(fs[0], '/tmp/_c.wav')
        y = make_loop(x)
        sf.write(os.path.join(OUT, f'{key}.wav'), y.astype(np.float32), SR, subtype='PCM_16')
        report[key] = [dict(file=f'{key}.wav', src=os.path.basename(fs[0]), dur=round(len(y) / SR, 1), note='петля')]
        continue
    allt = []
    for f in fs:
        x, raw = decode_clean(f, '/tmp/_c.wav', ex['nr'])
        ts, floor = takes(x, raw, prof, ex)
        for t in ts: t['src'] = os.path.basename(f); t['floor'] = floor
        allt += ts
    good = [t for t in allt if not t['clipped'] and not t['too_long'] and t['dur'] >= ex['mindur'] and t['snr'] >= ex['thr'] + 4]
    # сначала дубли с тишиной на обоих краях, внутри — по чистоте записи
    good.sort(key=lambda t: (max(t['head'], t['tail']) > 6, -t['snr']))
    keep = sorted(good[:prof[2]], key=lambda t: (t['src'], t['start']))
    report[key] = []
    for i, t in enumerate(keep, 1):
        name = f'{key}_{i:02d}.wav'
        sf.write(os.path.join(OUT, name), finish(t['seg'].copy(), prof[3]).astype(np.float32), SR, subtype='PCM_16')
        report[key].append(dict(file=name, src=t['src'], at=round(t['start'], 2), dur=round(t['dur'], 2), snr=round(t['snr'], 1), edges_db=(round(t['head'], 1), round(t['tail'], 1))))
    report[key + ' (отброшено)'] = dict(всего=len(allt), перегруз=sum(t['clipped'] for t in allt), тихие_или_короткие=len(allt) - len(good) - sum(t['clipped'] for t in allt))

print(json.dumps(report, ensure_ascii=False, indent=1, default=float))
