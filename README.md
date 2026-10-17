# Dofus Companion

Un compagnon pour **Dofus 3 (Unity)** sous Windows : un petit panneau toujours visible par-dessus le jeu qui
t'explique **comment battre chaque boss** — comment lever son invulnérabilité, dans quel ordre, quels pièges
éviter — avec la source de chaque information.

> Outil indépendant, **non affilié à Ankama ni autorisé par Ankama**. Il ne touche jamais au jeu : il ne lit
> pas sa mémoire, ne modifie aucun fichier, n'envoie aucune touche ni aucun clic. Tu joues, il t'informe.

---

## Télécharger et lancer

1. Va sur la page **[Releases](https://github.com/nbk265117/goldboss/releases/latest)**.
2. Télécharge le fichier **`DofusCompanion-0.1.0-win-x64.zip`** (≈ 80 Mo).
3. Fais un clic droit sur le zip → **Extraire tout…** (par exemple dans `Documents\DofusCompanion`).
4. Ouvre le dossier extrait et double-clique sur **`DofusCompanion.exe`**.

C'est tout : aucune installation, aucun droit administrateur, rien d'autre à télécharger (.NET est inclus).

**Premier lancement :** il peut prendre jusqu'à une minute (installation des données + analyse des fichiers
par Windows). Les suivants prennent environ 1 à 2 secondes.

### Si Windows bloque l'application

L'application n'est pas encore signée numériquement, Windows peut donc se méfier :

- **« Windows a protégé votre ordinateur »** (SmartScreen) → clique sur **Informations complémentaires**, puis
  **Exécuter quand même**.
- **« Une stratégie de contrôle d'application a bloqué ce fichier »** → c'est *Smart App Control* (Windows 11).
  Il bloque les programmes non signés qu'il ne connaît pas et ne propose pas d'exception par application.
  La seule solution est de le désactiver (*Sécurité Windows › Contrôle des applications et du navigateur ›
  Smart App Control*). Attention : selon ta version de Windows, il peut être impossible de le réactiver
  ensuite sans réinstaller Windows. À toi de décider.

### Configuration requise

- Windows 10 (version 2004 ou plus récente) ou Windows 11, 64 bits.
- Pour la lecture de captures d'écran : le module Windows « Reconnaissance optique de caractères » en
  français (présent par défaut sur un Windows en français ; sinon *Paramètres › Heure et langue › Langue ›
  Français › Options de langue*).

---

## Utilisation

| Action | Comment |
|---|---|
| Afficher / masquer l'overlay | `Ctrl+Shift+F9` ou le bouton dans la fenêtre principale |
| Chercher un boss ou un donjon | Tape son nom dans l'overlay, puis **Entrée** |
| Indiquer le tour actuel | Champ **Tour** de l'overlay (pour les conditions « tour pair », « à partir du tour 7 »…) |
| Lire une capture d'écran | Icône image de l'overlay, ou onglet **Analyse de capture** (fichier, ou `Win+Maj+S` puis **Coller**) |
| Mode compact / détaillé | Icône d'agrandissement de l'overlay |
| Clic traversant (l'overlay ignore la souris) | `Ctrl+Shift+F10` (même raccourci pour reprendre la main) |

Conseils :
- Joue en mode **fenêtré** ou **plein écran fenêtré** pour que l'overlay reste au-dessus du jeu.
- Pour montrer l'overlay sur **Discord**, partage ton **écran entier** (pas seulement la fenêtre Dofus).
- La capture d'écran à la demande est **désactivée par défaut** : *Paramètres › Capture et confidentialité*.
- Les raccourcis se changent dans **Paramètres**.

---

## Ce que contient l'application

- **131 donjons** (dont les 4 dimensions divines), **224 boss** dont **80 cibles d'avis de recherche**.
- **604 mécaniques** rédigées à partir des guides de *[Dofus pour les Noobs](https://www.dofuspourlesnoobs.com)*,
  chacune avec un lien vers sa page et une courte citation vérifiée mot pour mot.
- Chaque mécanique affiche son niveau de fiabilité : **Vérifié** ou **Rapporté par la communauté**.
- Une grande partie des guides date d'avant Dofus 3 : ces mécaniques sont marquées **« à revérifier »**.
- Rien n'est inventé : un boss sans mécanique documentée affiche « Aucune mécanique documentée ».

Tu peux ajouter tes propres mécaniques (onglet **Connaissances**) ou importer un fichier de connaissances.

## Vie privée

- Aucune connexion internet, aucune télémétrie, aucun compte demandé. Seul un clic sur « Ouvrir la source »
  ouvre la page du guide dans ton navigateur.
- Les captures sont analysées sur ton PC et ne sont pas conservées (sauf si tu l'actives).
- Tes données sont dans `%LOCALAPPDATA%\DofusCompanion`.

## Mettre à jour / désinstaller

- **Mettre à jour :** télécharge la nouvelle version et remplace le dossier. Tes données sont conservées.
- **Désinstaller :** supprime le dossier de l'application, et `%LOCALAPPDATA%\DofusCompanion` pour effacer
  aussi tes données.

---

## Pour les développeurs

Prérequis : [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/nbk265117/goldboss.git
cd goldboss
.\scripts\build.ps1      # compile (Release)
.\scripts\test.ps1       # lance les tests
.\scripts\publish.ps1    # crée le zip dans artifacts\publish
```

Documentation technique : [`docs/`](docs) — architecture, format des connaissances, tests, limites connues.

---

Les textes des mécaniques sont reformulés à partir des guides de *Dofus pour les Noobs*, avec attribution et
lien vers chaque page. DOFUS est une marque d'Ankama Games. Aucune ressource graphique du jeu n'est incluse.
