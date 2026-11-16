# Camembert (version native)

Analyseur d'espace disque pour Windows 10 et 11 (x64) : C# / .NET 10 / WPF / SkiaSharp, sans serveur.
Gratuit, portable, hors ligne : ni installation, ni compte, ni télémétrie.
État : **lots 1 à 4** du cahier des charges.

## Lancer

**Version portable** : décompresser `Camembert-portable-win-x64.zip` n'importe où, puis double-cliquer sur
`Camembert.exe`. Le runtime .NET est inclus : rien à installer.

`Camembert.exe <dossier>` lance directement l'analyse de ce dossier (`Camembert.exe C:` analyse tout le disque C).

**Depuis les sources** (SDK .NET 10) :

```
dotnet build -c Release
dotnet test
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

`build\publish.ps1` produit `artifacts\Camembert-portable-win-x64.zip`. Le workflow `.github/workflows/build.yml`
fait de même sur GitHub (tests, puis ZIP en artefact ; un tag `v*` crée une version avec le ZIP joint).

Tests : le test du moteur NTFS sur disque réel est ignoré hors d'une session administrateur ; le test de la Corbeille
envoie de petits fichiers temporaires dans la Corbeille puis les en retire.

## Fichiers écrits par Camembert

Tous à côté de `Camembert.exe`, rien dans le registre :

- `Camembert.settings.json` : réglages (thème, animations), créé au premier changement. Si le dossier n'est pas
  inscriptible, la fenêtre Paramètres le dit et le réglage ne vaut que pour la session.
- `journaux\camembert-AAAAMMJJ.log` : journal local (analyses, moteur utilisé, éléments envoyés à la Corbeille,
  erreurs), dix jours conservés, jamais envoyé. Si le dossier n'est pas inscriptible : `%LOCALAPPDATA%\Camembert\journaux`.

## Paramètres

Bouton « Paramètres » (accueil et barre supérieure) :

- **Thème** : selon Windows (par défaut, suit les changements de Windows en direct), clair ou sombre.
- **Animations du diagramme** : désactivables. Elles restent aussi coupées si l'option « Effets d'animation » de
  Windows est désactivée.

## Deux moteurs d'analyse

| | Analyse rapide NTFS | Analyse standard |
|---|---|---|
| Principe | Lit la table de fichiers (MFT) du volume d'un seul trait | Parcourt les dossiers un à un, en parallèle |
| Pour | Un volume NTFS entier | Tout dossier, tout système de fichiers |
| Droits | Administrateur | Aucun |
| Voit | Tout le volume, quels que soient les droits d'accès | Seulement ce que l'utilisateur peut lire |
| Liens physiques | Listés sous chaque nom, taille comptée une seule fois | Comptés une fois par nom |
| Fichiers système NTFS (`$MFT`…) | Visibles à la racine | Invisibles |

Sur l'accueil, un clic sur un volume NTFS lance l'analyse rapide ; le lien « Analyse standard, sans droits
d'administrateur » sous chaque volume lance l'autre. « Choisir un dossier… » utilise toujours l'analyse standard.
Le moteur réellement utilisé est écrit dans la barre d'état.

**Élévation.** L'interface ne tourne jamais en administrateur. Pour l'analyse rapide, elle relance son propre
exécutable avec élévation (`--analyse-elevee`, invite UAC) ; ce second processus lit la MFT en lecture seule et
renvoie l'arbre par un canal nommé. Il n'accepte aucune commande : fermer le canal l'arrête. Si l'élévation est
refusée ou si la lecture échoue, l'analyse standard prend le relais et la barre d'état le dit.

## Utilisation

- **Diagramme** : le disque central est le dossier courant, chaque anneau un niveau (6 au plus).
  Survol : nom, taille, pourcentage, chemin. Clic : sélection, reportée dans la liste.
  Double-clic sur un dossier : y entrer. Clic au centre ou touche Retour arrière : remonter.
  **Molette** : vers l'avant, entrer dans le dossier survolé ; vers l'arrière, remonter (la molette navigue, elle ne zoome pas).
  Clic droit : entrer, ouvrir dans l'Explorateur, copier le chemin.
  Le changement de dossier est animé (250 ms) ; l'animation suit le réglage Windows « Effets d'animation ».
- **Liste** (onglet « Contenu du dossier ») : nom, taille, pourcentage, type, date de modification. Clic sur un
  en-tête : tri, second clic : ordre inverse. Sélection multiple avec Ctrl et Maj. Double-clic ou Entrée : entrer
  dans un dossier. Clic droit : mêmes actions ; Ctrl+C copie les chemins sélectionnés.
- **Recherche** (onglet « Recherche » ou champ en haut à droite) : porte sur toute l'analyse, sans relire le disque.
  Critères cumulables : partie du nom (sans distinction de casse), extensions (`mp4, mkv`), catégorie, fichiers ou
  dossiers, taille minimale et maximale (`500 Mo`, `1,5 Go` ; un nombre seul est en Mo).
  Double-clic sur un résultat : l'afficher dans son dossier, dans le diagramme et la liste.
- **Fil d'Ariane** : clic sur un niveau pour y revenir.
- **Élément choisi** : le dernier élément sélectionné, dans le diagramme ou dans une liste, est rappelé sous le
  diagramme avec trois boutons : « Aperçu », « Ajouter au panier », « Ouvrir dans l'Explorateur ».
- **Aperçu** (bouton, clic droit ou touche Espace dans une liste) : image (JPEG, PNG, GIF, BMP, WebP, TIFF), texte
  (64 Ko au plus, encodage détecté), PDF page par page, vidéo (durée, dimensions, miniature) ; pour le reste, nom,
  taille, dates et chemin. Un fichier cloud non téléchargé n'est jamais lu, pour ne pas déclencher son téléchargement.

## Panier et suppression

Le panier, en bas de la fenêtre, rassemble ce que vous envisagez de supprimer. Y mettre un élément ne supprime rien.

1. Choisir un élément puis « Ajouter au panier » (ou clic droit, ou touche Suppr dans une liste).
   Un dossier remplace ce qu'il contient déjà dans le panier : le total ne compte rien deux fois.
2. « Mettre à la Corbeille… » affiche la liste et demande confirmation (« Non » par défaut).
3. Chaque élément part vers la **Corbeille Windows** (API Shell `IFileOperation`) : il reste restaurable.
4. La Corbeille étant sur le même disque, l'espace n'est réellement libéré qu'en la vidant (dans Windows ;
   Camembert ne vide jamais la Corbeille).
5. Les tailles sont mises à jour partout sans nouvelle analyse ; ce qui n'a pas pu être supprimé reste dans le
   panier, avec la raison.

Garde-fous :

- Jamais de suppression définitive par l'application. Sur un disque sans Corbeille (clé USB, réseau), la suppression
  est refusée. Si un élément est trop gros pour la Corbeille, c'est Windows qui avertit et demande.
- Emplacements protégés, refusés dès l'ajout au panier : tout élément à la racine d'un disque ; `Windows`,
  `Program Files`, `ProgramData` en entier ; `Users`, chaque profil et ses éléments directs (`Documents`, `Bureau`…) ;
  `AppData\Local`, `Roaming` et `LocalLow` eux-mêmes ; `System Volume Information`, `$Recycle.Bin`, `Recovery` et
  les fichiers système NTFS.
- Juste avant la suppression, chaque élément est revérifié : toujours présent, toujours un fichier (ou un dossier),
  pas devenu un lien, même date de modification et même taille qu'à l'analyse. Sinon il est laissé en place avec le
  message « Modifié depuis l'analyse » : relancer l'analyse.
- Aucun contournement des droits : un fichier verrouillé ou protégé est signalé, pas forcé.

## Structure

| Projet | Rôle |
|---|---|
| `Camembert.Core` | Réglages (`SettingsStore`), messages d'erreur en français (`ErrorText`), arbre compact (`FileTree`), agrégation, modèle du sunburst, recherche (`TreeSearch`), panier (`Basket`), emplacements protégés (`DeletionRules`), contrat `IScanEngine` |
| `Camembert.Scanner` | `Standard/StandardScanEngine` et `Ntfs/NtfsScanEngine` (+ `MftIndex`, décodage des enregistrements) |
| `Camembert.Windows` | Volumes, Explorateur, Corbeille (`Shell/SafeDelete`), aperçus (`Preview/PreviewLoader`), analyse dans un processus élevé (`Elevation/ElevatedScan`) |
| `Camembert.App` | Fenêtre WPF, ViewModels, contrôle sunburst SkiaSharp, thèmes (`Themes/`), journal |

`FileTree` : tableaux parallèles indexés par identifiant de nœud (environ 55 octets par entrée, nom compris).
Les enfants d'un dossier s'ajoutent en un seul bloc (`AddChildren`), donc un parent précède toujours ses enfants.

## Règles d'analyse

- Jonctions et liens symboliques : ni suivis ni comptés (pas de boucle, pas de double comptage).
- Fichiers cloud non téléchargés (OneDrive) : 0 octet (analyse rapide : ce qui est réellement sur le disque).
- Dossier inaccessible (analyse standard) : marqué « (inaccessible) », compté pour 0, l'analyse continue.

## Limites connues

- Taille **logique** uniquement : l'espace réellement alloué (compression, fichiers creux, clusters) n'est pas affiché.
- Analyse rapide : flux de données alternatifs et index des dossiers non comptés ; un fichier à plusieurs liens
  physiques est attribué au premier nom rencontré, les autres valent 0.
- Analyse rapide : sa durée est celle de la lecture de la MFT entière (environ 1 Ko par fichier présent ou passé),
  précédée de l'écriture par Windows de ses données en attente ; sur un disque très sollicité, cette étape seule
  peut prendre plusieurs secondes.
- L'écart affiché avec l'espace utilisé selon Windows vient de ces points (et, en analyse standard, des dossiers inaccessibles).
- Les petits éléments (moins de 0,5° d'arc) sont regroupés en un segment gris dans le diagramme ; ils restent tous dans la liste.
- Le résultat s'affiche à la fin de l'analyse (pas de résultat partiel pendant l'analyse).
- La recherche garde en mémoire une copie en minuscules des noms (environ 25 octets par entrée), créée à la première recherche.
- Suppression : un dossier dont le contenu profond a changé sans que sa propre date change n'est pas détecté
  comme modifié (seule la date du dossier lui-même est comparée) ; il part alors à la Corbeille avec son contenu actuel.
- Suppression : un chemin de 260 caractères ou plus est refusé par la Corbeille Windows.
- Suppression d'un fichier à plusieurs liens physiques : l'espace n'est réellement libéré que lorsque tous ses noms
  ont disparu ; la taille « libérée » affichée est celle du nom qui la portait dans l'analyse.
- Après une analyse rapide, les fichiers que seul un administrateur peut lire sont visibles mais leur aperçu et leur
  suppression échouent (l'interface n'est pas élevée) : c'est signalé, pas contourné.
- Windows 10 version 2004 ou plus récent (exigence de SkiaSharp.Views.WPF 4).
- Les messages d'erreur venant de .NET sont remplacés par un texte français générique (« accès refusé »,
  « introuvable »…) ; ceux venant de Windows restent dans la langue du système.
- `glfw3.dll` (dépendance de SkiaSharp.Views.WPF par OpenTK) est livré mais inutilisé.

## Tests d'acceptation (section 9 du cahier des charges)

| Test | État | Comment |
|---|---|---|
| Lancement sans installation de .NET | Partiel | ZIP autonome : aucun fichier du .NET installé chargé, pas de `runtimeconfig.json`. Non essayé sur une machine sans .NET. |
| Analyse NTFS et comparaison avec un outil de référence | Partiel | Comparée à l'espace utilisé selon Windows (écart 7,6 Go sur 896 Go) et au moteur standard ; pas à WizTree. |
| Analyse d'un support exFAT ou FAT32 | Oui | Disques virtuels exFAT et FAT32 : contenu exact (noms accentués, chemin profond), écart avec Windows 22 à 60 Ko, envoi à la Corbeille du volume. |
| Annulation d'un scan sans blocage | Oui | 89 à 328 ms selon le moteur. |
| Dossier inaccessible sans plantage | Oui | Test automatique et 468 dossiers sur C:. |
| Jonctions, liens symboliques, hard links | Oui | Tests automatiques (moteur standard et MFT). |
| Navigation sunburst et synchronisation avec la liste | Oui | Vérifiée dans l'application. |
| Recherche et filtrage sur un grand volume | Oui | 4 millions d'entrées : 3 à 300 ms. |
| Sélection multiple sans double comptage | Oui | Panier : tests automatiques et application. |
| Envoi vers la Corbeille après confirmation | Oui | Test automatique et application (fichiers d'essai). |
| Refus des dossiers système critiques | Oui | Tests automatiques et application (`C:\Windows\Web`). |
| Prévisualisation image, texte, PDF | Oui | Tests automatiques et application. |
| Thèmes clair et sombre | Oui | Vérifiés sur la version décompressée du ZIP. |
| Exécution depuis un dossier décompressé | Oui | Démarrage en 0,8 à 1 s, analyse rapide comprise (relance élevée de l'exécutable). |

