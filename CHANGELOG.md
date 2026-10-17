# Historique des versions

## 0.5.1 — 17/10/2026

- La **capture d'écran est autorisée par défaut** (y compris pour les installations existantes) : le bouton
  **Détecter** marche tout de suite. Elle ne se fait toujours que quand tu cliques et peut être coupée dans les
  Paramètres.
- Le bouton **Détecter** a sa propre ligne, sous la barre de recherche de l'overlay.
- **Nouvelle icône** : un œuf de dragon noir moucheté d'or (dessin original), pour l'application, la barre des
  tâches et la zone de notification.
- Images : l'importateur DofusDB télécharge maintenant les images de **tous les boss** (y compris ceux qui ne sont
  connus que de DofusDB), un **donjon affiche l'image de son boss**, et `--all-images` (tous les monstres) relève
  automatiquement la limite de requêtes.
- Recherche : un donjon affiche le nombre de mécaniques de ses boss au lieu de « aucune mécanique documentée ».

## 0.5.0 — 17/10/2026

- **Bouton « Détecter » dans l'overlay** : un clic lit l'écran de Dofus (localement) et affiche directement les
  étapes du boss, du donjon, du monstre ou de la zone reconnu (priorité boss › donjon › monstre › zone). Les
  monstres reconnus sont ajoutés au combat en cours. Nécessite d'autoriser la capture d'écran dans les Paramètres.
- Zone de détection optionnelle : onglet **Analyse** › « Utiliser cette zone pour la détection auto ».
- Importateur DofusDB : corrige le plantage final quand le dossier `imports` n'existait pas encore.

## 0.4.0 — 17/10/2026

- **Nouveau nom : Dofus Gold Boss - DGB.** Programme `DofusGoldBoss.exe`, zip `DofusGoldBoss-<version>-win-x64.zip`,
  données dans `%LOCALAPPDATA%\DofusGoldBoss` (l'ancien dossier `DofusCompanion` est déplacé automatiquement au
  premier lancement : rien n'est perdu).
- Le numéro de version s'affiche dans les titres.

## 0.3.2 — 17/10/2026

- Le **numéro de version** s'affiche dans le titre de la fenêtre, l'en-tête, l'overlay et l'infobulle de l'icône de
  notification.

## 0.3.1 — 17/10/2026

- **Images des boss en une commande** : l'importateur DofusDB (`--images`) ne télécharge plus que les images des
  224 boss de l'application (quelques minutes au lieu de plusieurs heures) et dépose son résultat dans le dossier
  de l'application, qui **l'importe automatiquement** au démarrage.
- Les boutons « Ajouter / Coller / Retirer une image » sont retirés de la fiche : l'image s'affiche directement.

## 0.3.0 — 17/10/2026

- **Images des boss et monstres** : vignette dans les résultats de recherche (overlay et application), image en
  tête de la fiche dans l'overlay, grande image dans l'application avec les boutons « Ajouter une image… »,
  « Coller une image » et « Retirer l'image ». Sans image, une pastille avec l'initiale s'affiche.
- **Importateur DofusDB `--images`** : télécharge les images de tous les monstres et boss dans ton dossier local
  (usage personnel, jamais publiées). Un monstre DofusDB est fusionné avec le boss du même nom (ex. les cibles
  d'avis de recherche), qui récupère ainsi son image.
- **Prêt pour la signature** : l'application est publiée en un seul `DofusGoldBoss.exe` et utilise le SQLite
  signé de Windows ; c'est le seul fichier à signer.

## 0.2.0 — 17/10/2026

- **Nouveau style des étapes dans l'overlay** : étapes numérotées sur une frise, prérequis avec leur état
  (rempli / à vérifier), résultat attendu en vert avec la durée, pièges mortels en rouge, informations
  manquantes en italique.
- **Fiche d'un donjon = mécaniques de ses boss** : chercher « Mémoire d'Orukam » affiche directement les
  mécaniques du Roi Imagami (avant : « aucune mécanique documentée » pour tous les donjons).
- **Overlay visible dans Discord / OBS** : il n'est plus masqué des partages d'écran ; il se cache uniquement
  pendant la capture faite par Dofus Companion elle-même.
- Texte de l'overlay lisible : l'opacité ne s'applique plus qu'au fond, plus au texte.
- Raccourcis par défaut sur `Ctrl+Shift+F9/F10/F11` (n'empiètent plus sur les raccourcis des navigateurs).
- Recherche plus rapide sur les grosses bases.

## 0.1.0 — 17/10/2026

Première version : overlay, encyclopédie (131 donjons, 224 boss, 604 mécaniques sourcées), analyse de
captures d'écran (OCR Windows local), import/export de connaissances, interface FR/EN.
